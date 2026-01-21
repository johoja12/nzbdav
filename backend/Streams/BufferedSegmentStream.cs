using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using Serilog;
using System.IO;
using System.Collections.Concurrent;
using NzbWebDAV.Database;

namespace NzbWebDAV.Streams;

/// <summary>
/// High-performance buffered stream that maintains a read-ahead buffer of segments
/// for smooth, consistent streaming performance.
/// </summary>
public class BufferedSegmentStream : Stream
{
    // Enable detailed timing for benchmarks
    public static bool EnableDetailedTiming { get; set; } = false;

    // Global timing accumulators (reset when EnableDetailedTiming is set to true)
    private static long s_totalFetchTimeMs;
    private static long s_totalChannelWriteTimeMs;
    private static long s_totalChannelReadWaitMs;
    private static long s_orderingSpinCount;
    private static long s_orderingYieldCount;
    private static long s_orderingDelayCount;
    private static int s_peakActiveWorkers;
    private static int s_totalSegmentsFetched;
    private static int s_totalSegmentsRead;
    private static int s_streamCount;

    // Granular fetch timing
    private static long s_connectionAcquireTimeMs;
    private static long s_networkReadTimeMs;
    private static long s_bufferCopyTimeMs;

    /// <summary>
    /// Reset global timing accumulators
    /// </summary>
    public static void ResetGlobalTimingStats()
    {
        s_totalFetchTimeMs = 0;
        s_totalChannelWriteTimeMs = 0;
        s_totalChannelReadWaitMs = 0;
        s_orderingSpinCount = 0;
        s_orderingYieldCount = 0;
        s_orderingDelayCount = 0;
        s_peakActiveWorkers = 0;
        s_totalSegmentsFetched = 0;
        s_totalSegmentsRead = 0;
        s_streamCount = 0;
        s_connectionAcquireTimeMs = 0;
        s_networkReadTimeMs = 0;
        s_bufferCopyTimeMs = 0;
    }

    /// <summary>
    /// Get global timing statistics (aggregated across all streams)
    /// </summary>
    public static StreamTimingStats GetGlobalTimingStats() => new()
    {
        TotalFetchTimeMs = s_totalFetchTimeMs,
        TotalChannelWriteTimeMs = s_totalChannelWriteTimeMs,
        TotalChannelReadWaitMs = s_totalChannelReadWaitMs,
        OrderingSpinCount = s_orderingSpinCount,
        OrderingYieldCount = s_orderingYieldCount,
        OrderingDelayCount = s_orderingDelayCount,
        PeakActiveWorkers = s_peakActiveWorkers,
        TotalSegmentsFetched = s_totalSegmentsFetched,
        TotalSegmentsRead = s_totalSegmentsRead,
        StreamLifetimeMs = 0, // Not applicable for global stats
        ConnectionAcquireTimeMs = s_connectionAcquireTimeMs,
        NetworkReadTimeMs = s_networkReadTimeMs,
        BufferCopyTimeMs = s_bufferCopyTimeMs
    };

    private readonly Channel<PooledSegmentData> _bufferChannel;
    private readonly Task _fetchTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenSource _linkedCts;
    private readonly IDisposable[] _contextScopes;
    private readonly ConnectionUsageContext? _usageContext;
    private readonly INntpClient _client;

    private PooledSegmentData? _currentSegment;
    private int _currentSegmentPosition;
    private long _position;
    private bool _disposed;

    private int _totalFetchedCount;
    private int _totalReadCount;
    private int _bufferedCount; // Number of segments currently in memory buffer

    private int _nextIndexToRead = 0;
    private int _maxFetchedIndex = -1;
    private readonly ConnectionUsageType _streamType; // Tracks the type for priority: PlexPlayback > PlexBackground > BufferedStreaming
    private readonly int _totalSegments;

    // Dynamic straggler timeout tracking
    private long _successfulFetchTimeMs; // Total time for successful fetches (for average calculation)
    private int _successfulFetchCount;   // Number of successful fetches

    // Per-stream provider performance scoring (for smart deprioritization)
    // Key: provider index, Value: rolling window of recent results
    private readonly ConcurrentDictionary<int, ProviderStreamScore> _providerScores = new();

    private class ProviderStreamScore
    {
        // Rolling window size - track last N operations for success rate calculation
        private const int WindowSize = 30; // Reduced from 50 for faster response to failures

        // Circular buffer: true = success, false = failure
        private readonly bool[] _recentResults = new bool[WindowSize];
        private int _writeIndex; // Next position to write
        private int _totalOperations; // Total operations recorded (for partial window)

        // Use "recent failure weight" instead of pure consecutive count
        // Each failure adds 2 points, each success subtracts 1 point (min 0)
        // This ensures failures are "sticky" and don't immediately reset on success
        private int _failureWeight;

        public DateTimeOffset LastFailureTime;
        public DateTimeOffset CooldownUntil; // Provider is deprioritized until this time

        private readonly object _lock = new();

        /// <summary>
        /// Records a success in the rolling window.
        /// Decays failure weight by 1 (doesn't reset to 0).
        /// </summary>
        public void RecordSuccess()
        {
            lock (_lock)
            {
                _recentResults[_writeIndex] = true;
                _writeIndex = (_writeIndex + 1) % WindowSize;
                _totalOperations++;
                // Decay failure weight by 1, but never go below 0
                _failureWeight = Math.Max(0, _failureWeight - 1);
            }
        }

        /// <summary>
        /// Records a failure in the rolling window.
        /// Adds 2 to failure weight for faster escalation.
        /// </summary>
        public void RecordFailure()
        {
            lock (_lock)
            {
                _recentResults[_writeIndex] = false;
                _writeIndex = (_writeIndex + 1) % WindowSize;
                _totalOperations++;
                // Add 2 to failure weight (failures count double)
                _failureWeight += 2;
                LastFailureTime = DateTimeOffset.UtcNow;
            }
        }

        /// <summary>
        /// Gets the current failure weight for cooldown calculation.
        /// Range: 0 to unbounded, but realistically 0-20 in practice.
        /// </summary>
        public int FailureWeight => _failureWeight;

        /// <summary>
        /// Gets the success rate based on the rolling window of recent operations.
        /// Returns 1.0 (assume good) if no operations recorded yet.
        /// </summary>
        public double SuccessRate
        {
            get
            {
                lock (_lock)
                {
                    var count = Math.Min(_totalOperations, WindowSize);
                    if (count == 0) return 1.0; // Assume good until proven otherwise

                    var successCount = 0;
                    for (int i = 0; i < count; i++)
                    {
                        if (_recentResults[i]) successCount++;
                    }
                    return (double)successCount / count;
                }
            }
        }

        /// <summary>
        /// Gets the total operations in the current window (for logging).
        /// </summary>
        public int WindowOperations => Math.Min(_totalOperations, WindowSize);

        /// <summary>
        /// Gets the failure count in the current window (for logging).
        /// </summary>
        public int WindowFailures
        {
            get
            {
                lock (_lock)
                {
                    var count = Math.Min(_totalOperations, WindowSize);
                    var failures = 0;
                    for (int i = 0; i < count; i++)
                    {
                        if (!_recentResults[i]) failures++;
                    }
                    return failures;
                }
            }
        }

        public bool IsInCooldown => DateTimeOffset.UtcNow < CooldownUntil;
    }

    public int BufferedCount => _bufferedCount;

    // Detailed timing metrics (only collected when EnableDetailedTiming = true)
    private long _totalFetchTimeMs;
    private long _totalDecodeTimeMs;
    private long _totalChannelWriteTimeMs;
    private long _totalChannelReadWaitMs;
    private long _orderingSpinCount;
    private long _orderingYieldCount;
    private long _orderingDelayCount;
    private int _peakActiveWorkers;
    private int _activeWorkers;
    private readonly Stopwatch _streamLifetime = Stopwatch.StartNew();

    /// <summary>
    /// Get timing statistics (only meaningful when EnableDetailedTiming = true)
    /// </summary>
    public StreamTimingStats GetTimingStats() => new()
    {
        TotalFetchTimeMs = _totalFetchTimeMs,
        TotalDecodeTimeMs = _totalDecodeTimeMs,
        TotalChannelWriteTimeMs = _totalChannelWriteTimeMs,
        TotalChannelReadWaitMs = _totalChannelReadWaitMs,
        OrderingSpinCount = _orderingSpinCount,
        OrderingYieldCount = _orderingYieldCount,
        OrderingDelayCount = _orderingDelayCount,
        PeakActiveWorkers = _peakActiveWorkers,
        TotalSegmentsFetched = _totalFetchedCount,
        TotalSegmentsRead = _totalReadCount,
        StreamLifetimeMs = _streamLifetime.ElapsedMilliseconds
    };

    public class StreamTimingStats
    {
        public long TotalFetchTimeMs { get; init; }
        public long TotalDecodeTimeMs { get; init; }
        public long TotalChannelWriteTimeMs { get; init; }
        public long TotalChannelReadWaitMs { get; init; }
        public long OrderingSpinCount { get; init; }
        public long OrderingYieldCount { get; init; }
        public long OrderingDelayCount { get; init; }
        public long ConnectionAcquireTimeMs { get; init; }
        public long NetworkReadTimeMs { get; init; }
        public long BufferCopyTimeMs { get; init; }
        public int PeakActiveWorkers { get; init; }
        public int TotalSegmentsFetched { get; init; }
        public int TotalSegmentsRead { get; init; }
        public long StreamLifetimeMs { get; init; }

        public void Print()
        {
            Console.WriteLine("\n══════════════════════════════════════════════════════════════");
            Console.WriteLine("  BUFFERED STREAM TIMING BREAKDOWN");
            Console.WriteLine("══════════════════════════════════════════════════════════════");
            Console.WriteLine($"  Stream Lifetime:        {StreamLifetimeMs,8} ms");
            Console.WriteLine($"  Segments Fetched:       {TotalSegmentsFetched,8}");
            Console.WriteLine($"  Segments Read:          {TotalSegmentsRead,8}");
            Console.WriteLine("──────────────────────────────────────────────────────────────");
            Console.WriteLine("  FETCH PHASE (cumulative across all workers):");
            Console.WriteLine($"    Total Fetch Time:     {TotalFetchTimeMs,8} ms");
            Console.WriteLine($"    Avg per Segment:      {(TotalSegmentsFetched > 0 ? TotalFetchTimeMs / TotalSegmentsFetched : 0),8} ms");
            Console.WriteLine("  FETCH BREAKDOWN:");
            Console.WriteLine($"    Connection Acquire:   {ConnectionAcquireTimeMs,8} ms ({(TotalFetchTimeMs > 0 ? ConnectionAcquireTimeMs * 100 / TotalFetchTimeMs : 0)}%)");
            Console.WriteLine($"    Network Read:         {NetworkReadTimeMs,8} ms ({(TotalFetchTimeMs > 0 ? NetworkReadTimeMs * 100 / TotalFetchTimeMs : 0)}%)");
            Console.WriteLine($"    Buffer Copy:          {BufferCopyTimeMs,8} ms ({(TotalFetchTimeMs > 0 ? BufferCopyTimeMs * 100 / TotalFetchTimeMs : 0)}%)");
            var unaccounted = TotalFetchTimeMs - ConnectionAcquireTimeMs - NetworkReadTimeMs - BufferCopyTimeMs;
            Console.WriteLine($"    Other/Overhead:       {unaccounted,8} ms ({(TotalFetchTimeMs > 0 ? unaccounted * 100 / TotalFetchTimeMs : 0)}%)");
            Console.WriteLine("──────────────────────────────────────────────────────────────");
            Console.WriteLine("  ORDERING PHASE:");
            Console.WriteLine($"    Channel Write Time:   {TotalChannelWriteTimeMs,8} ms");
            Console.WriteLine($"    Spin Waits:           {OrderingSpinCount,8}");
            Console.WriteLine($"    Yield Waits:          {OrderingYieldCount,8}");
            Console.WriteLine($"    Delay Waits:          {OrderingDelayCount,8}");
            Console.WriteLine("──────────────────────────────────────────────────────────────");
            Console.WriteLine("  CONSUMER PHASE:");
            Console.WriteLine($"    Channel Read Wait:    {TotalChannelReadWaitMs,8} ms");
            Console.WriteLine("──────────────────────────────────────────────────────────────");
            Console.WriteLine($"  Peak Active Workers:    {PeakActiveWorkers,8}");
            Console.WriteLine("══════════════════════════════════════════════════════════════");
        }
    }

    // Track corrupted segments for health check triggering
    private readonly long[]? _segmentSizes;
    private readonly List<(int Index, string SegmentId)> _corruptedSegments = new();
    private int _lastSuccessfulSegmentSize = 0;

    // Track which providers have failed for each segment (for straggler retry with different provider)
    private readonly ConcurrentDictionary<int, HashSet<int>> _failedProvidersPerSegment = new();

    // NOTE: Batch segment assignment and worker-provider affinity were tested but caused more duplicate
    // fetches and slower performance. The dynamic availability-ratio selection in
    // MultiProviderNntpClient.GetBalancedProviders() is more effective at distributing load.
    // See commit history for the attempted implementation.

    public BufferedSegmentStream(
        string[] segmentIds,
        long fileSize,
        INntpClient client,
        int concurrentConnections,
        int bufferSegmentCount,
        CancellationToken cancellationToken,
        ConnectionUsageContext? usageContext = null,
        long[]? segmentSizes = null)
    {
        _usageContext = usageContext;
        _segmentSizes = segmentSizes;
        _client = client;
        // Ensure buffer is large enough to handle stalls and jitter better.
        bufferSegmentCount = Math.Max(bufferSegmentCount, concurrentConnections * 10);

        // Create bounded channel for buffering
        var channelOptions = new BoundedChannelOptions(bufferSegmentCount)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        };
        _bufferChannel = Channel.CreateBounded<PooledSegmentData>(channelOptions);

        // Link cancellation tokens and preserve context
        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

        // Copy all contexts from the original token to the linked token
        // Store context scopes so they live for the duration of the stream
        _contextScopes = new[]
        {
            _linkedCts.Token.SetScopedContext(cancellationToken.GetContext<LastSuccessfulProviderContext>()),
            _linkedCts.Token.SetScopedContext(usageContext ?? cancellationToken.GetContext<ConnectionUsageContext>())
        };
        var contextToken = _linkedCts.Token;

        // NOTE: Batch segment assignment was tested but caused more duplicate fetches.
        // The availability-ratio provider selection in MultiProviderNntpClient.GetBalancedProviders
        // is more effective at distributing load. Keeping this disabled.
        // InitializeSegmentAssignments(segmentIds.Length, client, concurrentConnections);

        // Start background fetcher
        _fetchTask = Task.Run(async () =>
        {
            await FetchSegmentsAsync(segmentIds, client, concurrentConnections, bufferSegmentCount, contextToken)
                .ConfigureAwait(false);
        }, contextToken);

        // Start background reporter
        if (_usageContext != null)
        {
            _ = Task.Run(async () => {
                try {
                    while (!contextToken.IsCancellationRequested) {
                        await Task.Delay(1000, contextToken).ConfigureAwait(false);
                        UpdateUsageContext();
                    }
                } catch {}
            }, contextToken);
        }

        Length = fileSize;
        _totalSegments = segmentIds.Length;

        // Register stream with connection limiter for provider affinity priority tracking
        // Priority order: PlexPlayback > PlexBackground > BufferedStreaming
        _streamType = usageContext?.UsageType ?? ConnectionUsageType.BufferedStreaming;
        StreamingConnectionLimiter.Instance?.RegisterStream(_streamType);
    }

    private void UpdateUsageContext()
    {
        if (_usageContext?.DetailsObject == null) return;

        // Update the shared details object
        var details = _usageContext.Value.DetailsObject;
        details.BufferedCount = _bufferedCount;
        details.BufferWindowStart = _nextIndexToRead;
        details.BufferWindowEnd = Math.Max(_nextIndexToRead, _maxFetchedIndex);
        details.TotalSegments = _totalSegments;
        // Use BaseByteOffset (set by NzbFileStream) + current position for absolute byte position
        // FileSize is already set by NzbFileStream to total file size, don't overwrite it
        details.CurrentBytePosition = (details.BaseByteOffset ?? 0) + _position;

        // Occasionally trigger a UI update via ConnectionPool if possible
        var multiClient = GetMultiProviderClient(_client);
        if (multiClient != null)
        {
            foreach (var provider in multiClient.Providers)
            {
                provider.ConnectionPool.TriggerStatsUpdate();
            }
        }
    }

    private async Task FetchSegmentsAsync(
        string[] segmentIds,
        INntpClient client,
        int concurrentConnections,
        int bufferSegmentCount,
        CancellationToken ct)
    {
        try
        {
            // Priority channel for racing stragglers or retrying preempted segments
            var urgentChannel = Channel.CreateUnbounded<(int index, string segmentId)>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false // Multiple sources (monitor, workers)
            });

            // Standard queue
            var segmentQueue = Channel.CreateBounded<(int index, string segmentId)>(new BoundedChannelOptions(bufferSegmentCount)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true
            });

            // Track active assignments: Index -> (StartTime, Cts, WorkerId)
            var activeAssignments = new ConcurrentDictionary<int, (DateTimeOffset StartTime, CancellationTokenSource Cts, int WorkerId)>();
            
            // Track which segments are currently being raced to avoid double-racing
            var racingIndices = new ConcurrentDictionary<int, bool>();

            // Producer: Queue all segment IDs
            var producerTask = Task.Run(async () =>
            {
                try
                {
                    for (int i = 0; i < segmentIds.Length; i++)
                    {
                        if (ct.IsCancellationRequested) break;
                        await segmentQueue.Writer.WriteAsync((i, segmentIds[i]), ct).ConfigureAwait(false);
                    }
                    segmentQueue.Writer.Complete();
                }
                catch (Exception ex)
                {
                    segmentQueue.Writer.Complete(ex);
                }
            }, ct);

            // Straggler Monitor Task - checks ALL active workers, cancels any running > dynamic timeout
            var monitorTask = Task.Run(async () =>
            {
                try
                {
                    // Minimum timeout to avoid being too aggressive
                    const double minTimeoutSeconds = 5.0;
                    // Minimum samples required before enabling straggler detection
                    const int minSamplesForDetection = 10;

                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(100, ct).ConfigureAwait(false); // Check every 100ms

                        var now = DateTimeOffset.UtcNow;

                        // Calculate dynamic straggler timeout based on average successful fetch time
                        var fetchCount = Volatile.Read(ref _successfulFetchCount);
                        var fetchTimeMs = Volatile.Read(ref _successfulFetchTimeMs);

                        // Don't run straggler detection until we have enough samples
                        // This prevents killing workers during initial connection pool warmup
                        if (fetchCount < minSamplesForDetection)
                        {
                            continue;
                        }

                        var avgFetchTimeMs = (double)fetchTimeMs / fetchCount;
                        // Timeout = avg * 3, with minimum floor of 5 seconds
                        var stragglerTimeoutSeconds = Math.Max(minTimeoutSeconds, (avgFetchTimeMs / 1000.0) * 3.0);

                        // Check ALL active workers, not just the one blocking the consumer
                        foreach (var kvp in activeAssignments)
                        {
                            var segmentIndex = kvp.Key;
                            var assignment = kvp.Value;
                            var duration = now - assignment.StartTime;

                            // Skip if not stalled or already being raced
                            if (duration.TotalSeconds <= stragglerTimeoutSeconds) continue;
                            if (racingIndices.ContainsKey(segmentIndex)) continue;

                            // This worker is stalled - cancel it and retry on different provider
                            var workerContext = assignment.Cts.Token.GetContext<ConnectionUsageContext>();
                            var hasForcedProvider = workerContext.DetailsObject?.ForcedProviderIndex.HasValue ?? false;
                            var failedProviderIndex = workerContext.DetailsObject?.CurrentProviderIndex;

                            // Record which provider failed (so retry uses different provider)
                            // Skip if ForcedProviderIndex is set - there's only one provider allowed
                            if (failedProviderIndex.HasValue && !hasForcedProvider)
                            {
                                // Check how many providers we have and how many are already excluded
                                var multiClient = GetMultiProviderClient(client);
                                var totalProviders = multiClient?.Providers.Count ?? 1;
                                var currentExclusions = _failedProvidersPerSegment.TryGetValue(segmentIndex, out var existing) ? existing.Count : 0;

                                // Don't exclude if we'd leave no providers - let the last one keep trying
                                if (currentExclusions >= totalProviders - 1)
                                {
                                    Log.Debug("[BufferedStream] STRAGGLER: Segment {Index} on provider {Provider} running {Duration:F1}s but already excluded {Excluded}/{Total} providers. Letting it continue.",
                                        segmentIndex, failedProviderIndex.Value, duration.TotalSeconds, currentExclusions, totalProviders);
                                    continue; // Don't cancel, don't exclude - let it finish
                                }

                                Log.Debug("[BufferedStream] STRAGGLER: Segment {Index} on provider {Provider} running {Duration:F1}s (timeout: {Timeout:F1}s, avg: {Avg:F0}ms). Cancelling and retrying on different provider.",
                                    segmentIndex, failedProviderIndex.Value, duration.TotalSeconds, stragglerTimeoutSeconds, fetchCount > 0 ? (double)fetchTimeMs / fetchCount : 0);

                                _failedProvidersPerSegment.AddOrUpdate(
                                    segmentIndex,
                                    _ => new HashSet<int> { failedProviderIndex.Value },
                                    (_, set) => { lock (set) { set.Add(failedProviderIndex.Value); } return set; }
                                );

                                // Record straggler for per-stream cooldown (soft deprioritization for all segments)
                                RecordProviderStraggler(failedProviderIndex.Value, totalProviders);

                                // Record straggler failure to affinity service so slow providers are deprioritized globally
                                var affinityKey = workerContext.AffinityKey;
                                if (!string.IsNullOrEmpty(affinityKey))
                                {
                                    multiClient?.AffinityService?.RecordFailure(affinityKey, failedProviderIndex.Value);
                                }
                            }
                            else
                            {
                                Log.Debug("[BufferedStream] STRAGGLER: Segment {Index} running {Duration:F1}s (timeout: {Timeout:F1}s). Cancelling and retrying.",
                                    segmentIndex, duration.TotalSeconds, stragglerTimeoutSeconds);
                            }

                            // Cancel the stalled worker
                            assignment.Cts.Cancel();

                            // Re-queue the segment (high priority)
                            _ = urgentChannel.Writer.WriteAsync((segmentIndex, segmentIds[segmentIndex]), ct);
                            racingIndices.TryAdd(segmentIndex, true);
                        }
                    }
                }
                catch { /* Ignore cancellation */ }
            }, ct);

            // Lock-free segment ordering: workers write to slots, ordering task reads in order
            var segmentSlots = new PooledSegmentData?[segmentIds.Length];
            var nextIndexToWrite = 0;

            // Ordering task: reads slots in order and writes to buffer channel
            var orderingTask = Task.Run(async () =>
            {
                try
                {
                    var spinCount = 0;
                    while (nextIndexToWrite < segmentIds.Length && !ct.IsCancellationRequested)
                    {
                        var segment = Volatile.Read(ref segmentSlots[nextIndexToWrite]);
                        if (segment != null)
                        {
                            // Clear slot and write to channel
                            Volatile.Write(ref segmentSlots[nextIndexToWrite], null);

                            Stopwatch? writeWatch = null;
                            if (EnableDetailedTiming)
                            {
                                writeWatch = Stopwatch.StartNew();
                            }

                            // Try to write, but channel may be closed if stream disposed early
                            if (!_bufferChannel.Writer.TryWrite(segment))
                            {
                                // Channel is full or closed - try async write with cancellation
                                try
                                {
                                    await _bufferChannel.Writer.WriteAsync(segment, ct).ConfigureAwait(false);
                                }
                                catch (ChannelClosedException)
                                {
                                    // Channel was closed (stream disposed) - dispose segment and exit
                                    segment.Dispose();
                                    Log.Debug("[BufferedStream] Ordering task exiting - channel closed");
                                    return;
                                }
                            }

                            if (EnableDetailedTiming && writeWatch != null)
                            {
                                Interlocked.Add(ref _totalChannelWriteTimeMs, writeWatch.ElapsedMilliseconds);
                                Interlocked.Add(ref s_totalChannelWriteTimeMs, writeWatch.ElapsedMilliseconds);
                            }

                            nextIndexToWrite++;
                            spinCount = 0; // Reset spin count on success
                        }
                        else
                        {
                            // Slot not ready yet - use adaptive waiting
                            spinCount++;
                            if (spinCount < 10)
                            {
                                // Fast spin for first few iterations
                                Thread.SpinWait(100);
                                if (EnableDetailedTiming)
                                {
                                    Interlocked.Increment(ref _orderingSpinCount);
                                    Interlocked.Increment(ref s_orderingSpinCount);
                                }
                            }
                            else if (spinCount < 100)
                            {
                                // Yield to other threads
                                await Task.Yield();
                                if (EnableDetailedTiming)
                                {
                                    Interlocked.Increment(ref _orderingYieldCount);
                                    Interlocked.Increment(ref s_orderingYieldCount);
                                }
                            }
                            else
                            {
                                // Longer wait when truly idle
                                await Task.Delay(1, ct).ConfigureAwait(false);
                                if (EnableDetailedTiming)
                                {
                                    Interlocked.Increment(ref _orderingDelayCount);
                                    Interlocked.Increment(ref s_orderingDelayCount);
                                }
                                spinCount = 50; // Reset to middle value
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log.Error(ex, "[BufferedStream] Error in ordering task");
                }
            }, ct);

            // Consumers
            var workers = Enumerable.Range(0, concurrentConnections)
                .Select(async workerId =>
                {
                    // Reusable buffer for worker loop
                    while (!ct.IsCancellationRequested)
                    {
                        (int index, string segmentId) job = default;
                        bool isUrgent = false;

                        try
                        {
                            // Priority 1: Urgent Channel
                            if (urgentChannel.Reader.TryRead(out job))
                            {
                                isUrgent = true;
                            }
                            // Priority 2: Standard Queue
                            else
                            {
                                // Wait for data on either channel
                                // We prefer standard queue usually, unless urgent arrives.
                                // Since we can't easily wait on both, we wait on standard.
                                // If standard is empty, we wait. If urgent comes, we might miss it until standard has item?
                                // To fix this, we should really loop/delay or use a combined read.
                                // For simplicity: TryRead standard. If empty, WaitToReadAsync on both?
                                // Actually, producer fills standard fast. It's rarely empty unless EOF.
                                
                                if (!segmentQueue.Reader.TryRead(out job))
                                {
                                    // Check if we're done: standard queue completed AND urgent queue empty
                                    if (segmentQueue.Reader.Completion.IsCompleted)
                                    {
                                        // Try urgent one more time before exiting
                                        if (urgentChannel.Reader.TryRead(out job))
                                        {
                                            isUrgent = true;
                                        }
                                        else
                                        {
                                            // Both truly empty - worker can exit
                                            break;
                                        }
                                    }
                                    else
                                    {
                                        // Wait for item available on EITHER
                                        var t1 = segmentQueue.Reader.WaitToReadAsync(ct).AsTask();
                                        var t2 = urgentChannel.Reader.WaitToReadAsync(ct).AsTask();

                                        await Task.WhenAny(t1, t2).ConfigureAwait(false);

                                        if (urgentChannel.Reader.TryRead(out job)) isUrgent = true;
                                        else if (!segmentQueue.Reader.TryRead(out job))
                                        {
                                            continue;
                                        }
                                    }
                                }
                            }

                            // Skip if already fetched (race condition handling)
                            if (Volatile.Read(ref segmentSlots[job.index]) != null) continue;

                            // Create job-specific CTS for preemption (no hard timeout - straggler monitor handles stuck segments)
                            using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                            // Get the base context - we share the DetailsObject to ensure stats updates
                            // (BufferedCount, CurrentBytePosition, etc.) are visible to the connection pool
                            var baseContext = ct.GetContext<ConnectionUsageContext>();

                            // Build per-job exclusion list from straggler failures
                            // Use struct-based exclusions to avoid race conditions between workers
                            HashSet<int>? jobExcludedProviders = null;
                            if (_failedProvidersPerSegment.TryGetValue(job.index, out var failed))
                            {
                                lock (failed)
                                {
                                    if (failed.Count > 0)
                                    {
                                        jobExcludedProviders = new HashSet<int>(failed);
                                    }
                                }
                                if (jobExcludedProviders != null)
                                {
                                    Log.Debug("[BufferedStream] Segment {Index} excluding providers [{Providers}] due to previous straggler failures",
                                        job.index, string.Join(",", jobExcludedProviders));
                                }
                            }

                            // Get providers in cooldown for soft deprioritization
                            var cooldownProviders = GetProvidersInCooldown();
                            var hasCooldown = cooldownProviders.Count > 0;

                            // Create a per-job context with exclusions and cooldown providers
                            // This prevents race conditions where workers overwrite each other's exclusions
                            var jobContext = baseContext.WithProviderAdjustments(
                                jobExcludedProviders,
                                hasCooldown ? cooldownProviders : null
                            );
                            using var _scope = jobCts.Token.SetScopedContext(jobContext);

                            var assignment = (DateTimeOffset.UtcNow, jobCts, workerId);
                            
                            // Track assignment
                            // Note: If racing, multiple workers might have same index. We just overwrite or ignore.
                            // We mainly care about having *at least one* active.
                            activeAssignments[job.index] = assignment;

                            // Track active workers for timing
                            if (EnableDetailedTiming)
                            {
                                var current = Interlocked.Increment(ref _activeWorkers);
                                int peak;
                                do
                                {
                                    peak = _peakActiveWorkers;
                                    if (current <= peak) break;
                                } while (Interlocked.CompareExchange(ref _peakActiveWorkers, current, peak) != peak);
                                // Also update global peak
                                do
                                {
                                    peak = s_peakActiveWorkers;
                                    if (current <= peak) break;
                                } while (Interlocked.CompareExchange(ref s_peakActiveWorkers, current, peak) != peak);
                            }

                            try
                            {
                                // Acquire a streaming connection permit from the global pool
                                // This ensures total streaming connections are shared across all active streams
                                var limiter = StreamingConnectionLimiter.Instance;
                                var hasPermit = false;
                                if (limiter != null)
                                {
                                    hasPermit = await limiter.AcquireAsync(TimeSpan.FromSeconds(60), jobCts.Token).ConfigureAwait(false);
                                    if (!hasPermit)
                                    {
                                        Log.Warning("[BufferedStream] Worker {WorkerId} timed out waiting for streaming permit for segment {Index}", workerId, job.index);
                                        continue; // Try again with next job
                                    }
                                }

                                try
                                {
                                    // Always track fetch time for dynamic straggler timeout
                                    var fetchWatch = Stopwatch.StartNew();

                                    var segmentData = await FetchSegmentWithRetryAsync(job.index, job.segmentId, segmentIds, client, jobCts.Token).ConfigureAwait(false);

                                    fetchWatch.Stop();
                                    var fetchTimeMs = fetchWatch.ElapsedMilliseconds;

                                    if (EnableDetailedTiming)
                                    {
                                        Interlocked.Add(ref _totalFetchTimeMs, fetchTimeMs);
                                        Interlocked.Add(ref s_totalFetchTimeMs, fetchTimeMs);
                                    }

                                    // Store result directly to slot (lock-free, first write wins)
                                    var existingSlot = Interlocked.CompareExchange(ref segmentSlots[job.index], segmentData, null);
                                    if (existingSlot == null)
                                    {
                                        // We won the race - update stats for dynamic straggler timeout
                                        Interlocked.Add(ref _successfulFetchTimeMs, fetchTimeMs);
                                        Interlocked.Increment(ref _successfulFetchCount);

                                        // Record provider success for per-stream scoring
                                        var successProviderIndex = jobContext.DetailsObject?.CurrentProviderIndex;
                                        if (successProviderIndex.HasValue)
                                        {
                                            RecordProviderSuccess(successProviderIndex.Value);
                                        }

                                        // Update max fetched using lock-free compare-exchange
                                        int currentMax;
                                        do
                                        {
                                            currentMax = _maxFetchedIndex;
                                            if (job.index <= currentMax) break;
                                        } while (Interlocked.CompareExchange(ref _maxFetchedIndex, job.index, currentMax) != currentMax);

                                        Interlocked.Increment(ref _bufferedCount);
                                        Interlocked.Increment(ref _totalFetchedCount);
                                        if (EnableDetailedTiming) Interlocked.Increment(ref s_totalSegmentsFetched);
                                        // Ordering task will pick up from slot and write to channel
                                    }
                                    else
                                    {
                                        // Lost the race (already fetched), dispose data
                                        Log.Debug("[BufferedStream] SEGMENT RACE LOST: Segment={SegmentIndex}, Worker={WorkerId}",
                                            job.index, workerId);
                                        segmentData.Dispose();
                                    }
                                }
                                finally
                                {
                                    // Release the streaming connection permit
                                    if (hasPermit && limiter != null)
                                    {
                                        limiter.Release();
                                    }
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                // If main CT not cancelled, we were preempted.
                                if (!ct.IsCancellationRequested)
                                {
                                    Log.Debug("[BufferedStream] Worker {WorkerId} preempted on segment {Index}.", workerId, job.index);
                                    // We DO NOT re-queue here; the monitor already re-queued it (or the race duplicate).
                                    // If we were the victim, monitor queued us. 
                                    // If we were the slow straggler being killed? We should probably re-queue just in case?
                                    // Monitor queued a duplicate. If we die, the duplicate runs. 
                                    // But monitor queues duplicate to URGENT.
                                    // If we were preempted, it means we were the VICTIM (high index).
                                    // Monitor re-queued us to URGENT. So we are fine.
                                }
                                else throw;
                            }
                            finally
                            {
                                activeAssignments.TryRemove(job.index, out _);
                                racingIndices.TryRemove(job.index, out _);
                                if (EnableDetailedTiming) Interlocked.Decrement(ref _activeWorkers);
                            }
                        }
                        catch (Exception ex)
                        {
                            // If an unrecoverable error occurs (like inability to zero-fill a missing segment),
                            // we must abort the stream so the consumer doesn't hang waiting for this index.
                            if (!ct.IsCancellationRequested)
                            {
                                Log.Error(ex, "[BufferedStream] Critical worker error for segment {Index}. Aborting stream.", job.index);
                                _bufferChannel.Writer.TryComplete(ex);
                                return; // Exit worker task
                            }
                        }
                    }
                })
                .ToList();

            await Task.WhenAll(workers).ConfigureAwait(false);

            // Wait for ordering task to finish writing all segments to channel
            // Give it a reasonable timeout in case something went wrong
            try
            {
                await orderingTask.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Log.Warning("[BufferedStream] Ordering task timed out. NextIndexToWrite={NextIndex}, TotalSegments={Total}",
                    nextIndexToWrite, segmentIds.Length);
            }
            catch (OperationCanceledException)
            {
                // Expected when stream is disposed early
            }

            _bufferChannel.Writer.TryComplete(); // Use TryComplete to avoid exception if already closed
        }
        catch (OperationCanceledException)
        {
            _bufferChannel.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[BufferedStream] Error in FetchSegmentsAsync");
            _bufferChannel.Writer.Complete(ex);
        }
    }

    /// <summary>
    /// Fetches a segment with retry logic for corruption/validation failures.
    /// Implements exponential backoff and graceful degradation.
    /// </summary>
    private async Task<PooledSegmentData> FetchSegmentWithRetryAsync(
        int index,
        string segmentId,
        string[] segmentIds,
        INntpClient client,
        CancellationToken ct)
    {
        const int maxRetries = 3;
        Exception? lastException = null;
        var jobName = _usageContext?.DetailsObject?.Text ?? "Unknown";
        var fetchStartTime = Stopwatch.StartNew();

        // Get initial exclusions from context (set by worker for straggler retries)
        var baseContext = ct.GetContext<ConnectionUsageContext>();
        var initialExclusions = baseContext.ExcludedProviderIndices;

        // Track providers that failed for this segment to exclude on retries (within this method)
        var retryExclusions = new HashSet<int>();

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            // Exponential backoff: 0s, 1s, 2s, 4s
            if (attempt > 0)
            {
                var allExclusions = MergeExclusions(initialExclusions, retryExclusions);
                var delaySeconds = Math.Pow(2, attempt - 1);
                Log.Warning("[BufferedStream] RETRY: Job={Job}, Segment={SegmentIndex}/{TotalSegments} (ID: {SegmentId}), Attempt={Attempt}/{MaxRetries}, Waiting {Delay}s before retry, ExcludingProviders=[{ExcludedProviders}]",
                    jobName, index, segmentIds.Length, segmentId, attempt + 1, maxRetries, delaySeconds, string.Join(",", allExclusions));
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
            }

            // Merge straggler exclusions with retry-specific exclusions
            var excludedProviders = MergeExclusions(initialExclusions, retryExclusions);

            // Only create a new scoped context if we have NEW retry-specific exclusions
            // On the first attempt (retryExclusions empty), the worker's context from line 757 already has correct exclusions
            // Creating a duplicate scope would overwrite it, then both would try to dispose the same key (causing warnings)
            IDisposable? retryScope = null;
            if (retryExclusions.Count > 0)
            {
                retryScope = ct.SetScopedContext(baseContext.WithExcludedProviders(excludedProviders));
            }

            Stream? stream = null;
            try
            {
                // Time connection acquisition
                Stopwatch? acquireWatch = null;
                if (EnableDetailedTiming) acquireWatch = Stopwatch.StartNew();

                var fetchHeaders = index == 0;
                var multiClient = GetMultiProviderClient(client);

                if (multiClient != null)
                {
                    // Use balanced provider selection based on availability ratio
                    stream = await multiClient.GetBalancedSegmentStreamAsync(segmentId, fetchHeaders, ct).ConfigureAwait(false);
                }
                else
                {
                    stream = await client.GetSegmentStreamAsync(segmentId, fetchHeaders, ct).ConfigureAwait(false);
                }

                // IMPORTANT: Reset the fetch start time now that we have the stream.
                // This ensures straggler detection only considers data read time, not connection time.
                fetchStartTime.Restart();

                if (EnableDetailedTiming && acquireWatch != null)
                {
                    acquireWatch.Stop();
                    Interlocked.Add(ref s_connectionAcquireTimeMs, acquireWatch.ElapsedMilliseconds);
                }

                if (fetchHeaders && stream is YencHeaderStream yencStream && yencStream.ArticleHeaders != null)
                {
                    FileDate = yencStream.ArticleHeaders.Date;
                    if (_usageContext?.DetailsObject != null)
                    {
                        _usageContext.Value.DetailsObject.FileDate = FileDate;
                    }
                }

                // Rent a buffer and read the segment into it
                var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
                var totalRead = 0;

                // Time network read
                Stopwatch? readWatch = null;
                if (EnableDetailedTiming) readWatch = Stopwatch.StartNew();

                var readLoopWatch = Stopwatch.StartNew();
                var readLoopCount = 0;

                try
                {
                    while (true)
                    {
                        if (totalRead == buffer.Length)
                        {
                            // Resize
                            var newBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                            Buffer.BlockCopy(buffer, 0, newBuffer, 0, totalRead);
                            ArrayPool<byte>.Shared.Return(buffer);
                            buffer = newBuffer;
                        }

                        var readStartMs = readLoopWatch.ElapsedMilliseconds;
                        var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct).ConfigureAwait(false);
                        var readDurationMs = readLoopWatch.ElapsedMilliseconds - readStartMs;
                        readLoopCount++;

                        // Only log slow individual reads (> 1 second)
                        if (readDurationMs > 1000)
                        {
                            Log.Debug("[BufferedStream] SLOW READ: Segment={SegmentIndex}, ReadCount={ReadCount}, BytesRead={BytesRead}, ReadDuration={ReadDuration}ms",
                                index, readLoopCount, read, readDurationMs);
                        }

                        if (read == 0) break;
                        totalRead += read;
                    }

                    if (EnableDetailedTiming && readWatch != null)
                    {
                        readWatch.Stop();
                        Interlocked.Add(ref s_networkReadTimeMs, readWatch.ElapsedMilliseconds);
                    }

                    // Validate segment size against YENC header
                    if (stream is YencHeaderStream yencHeaderStream)
                    {
                        var expectedSize = yencHeaderStream.Header.PartSize;
                        if (totalRead != expectedSize)
                        {
                            // If we got significantly less data than expected, this is likely a corrupted or incomplete segment
                            // Treat this as an error rather than just a warning
                            if (totalRead < expectedSize * 0.9) // Less than 90% of expected size
                            {
                                ArrayPool<byte>.Shared.Return(buffer);
                                throw new InvalidDataException(
                                    $"Incomplete segment for {jobName}: Segment {index}/{segmentIds.Length} ({segmentId}) " +
                                    $"expected {expectedSize} bytes but got only {totalRead} bytes ({totalRead - expectedSize} byte deficit). " +
                                    $"This may indicate a timeout, network issue, or corrupted segment."
                                );
                            }

                            Log.Warning("[BufferedStream] SEGMENT SIZE MISMATCH: Job={Job}, Segment={SegmentIndex}/{TotalSegments} (ID: {SegmentId}), Expected={Expected} bytes, Got={Actual} bytes, Diff={Diff}",
                                jobName, index, segmentIds.Length, segmentId, expectedSize, totalRead, totalRead - expectedSize);
                        }
                    }

                    // Success! Return the segment
                    if (totalRead > _lastSuccessfulSegmentSize)
                    {
                        // Use loop for thread-safe max update
                        int initial, computed;
                        do
                        {
                            initial = _lastSuccessfulSegmentSize;
                            computed = Math.Max(initial, totalRead);
                        } while (initial != computed && Interlocked.CompareExchange(ref _lastSuccessfulSegmentSize, computed, initial) != initial);
                    }

                    return new PooledSegmentData(segmentId, buffer, totalRead);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    throw;
                }
            }
            catch (InvalidDataException ex)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ex.Message, ex, ct);

                lastException = ex;

                // Track which provider failed so we exclude it on retry
                var failedProviderIndex = baseContext.DetailsObject?.CurrentProviderIndex;
                if (failedProviderIndex.HasValue)
                {
                    retryExclusions.Add(failedProviderIndex.Value);
                }

                Log.Warning("[BufferedStream] CORRUPTION DETECTED: Job={Job}, Segment={SegmentIndex}/{TotalSegments} (ID: {SegmentId}), Attempt={Attempt}/{MaxRetries}, FailedProvider={FailedProvider}: {Message}",
                    jobName, index, segmentIds.Length, segmentId, attempt + 1, maxRetries, failedProviderIndex?.ToString() ?? "unknown", ex.Message);

                // Will retry on next iteration with failed provider excluded
            }
            catch (UsenetArticleNotFoundException ex)
            {
                // Don't retry for missing articles - this is permanent
                // Treat as terminal failure and proceed to graceful degradation (zero-fill)
                lastException = ex;
                Log.Warning("[BufferedStream] PERMANENT FAILURE: Job={Job}, Segment={SegmentIndex}/{TotalSegments} (ID: {SegmentId}): Article not found. Proceeding to zero-fill.",
                    jobName, index, segmentIds.Length, segmentId);
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ex.Message, ex, ct);

                lastException = ex;

                // Track which provider failed so we exclude it on retry
                var failedProviderIndex = baseContext.DetailsObject?.CurrentProviderIndex;
                if (failedProviderIndex.HasValue)
                {
                    retryExclusions.Add(failedProviderIndex.Value);
                }

                Log.Warning("[BufferedStream] ERROR FETCHING SEGMENT: Job={Job}, Segment={SegmentIndex}/{TotalSegments} (ID: {SegmentId}), Attempt={Attempt}/{MaxRetries}, FailedProvider={FailedProvider}: {Message}",
                    jobName, index, segmentIds.Length, segmentId, attempt + 1, maxRetries, failedProviderIndex?.ToString() ?? "unknown", ex.Message);
                // Will retry on next iteration with failed provider excluded
            }
            finally
            {
                if (stream != null)
                    await stream.DisposeAsync().ConfigureAwait(false);
                retryScope?.Dispose();
            }
        }

        // All retries failed - check if graceful degradation is disabled (e.g., for benchmarks)
        var disableGracefulDegradation = _usageContext?.DetailsObject?.DisableGracefulDegradation ?? false;
        if (disableGracefulDegradation)
        {
            var reason = lastException?.Message ?? "Unknown error after all retries exhausted";
            Log.Warning("[BufferedStream] PERMANENT FAILURE (graceful degradation disabled): Job={Job}, Segment={SegmentIndex}/{TotalSegments} (ID: {SegmentId}) failed after {MaxRetries} attempts. Throwing exception. Last error: {LastError}",
                jobName, index, segmentIds.Length, segmentId, maxRetries, reason);

            throw new PermanentSegmentFailureException(index, segmentId, reason);
        }

        // Use graceful degradation (zero-fill)
        Log.Error("[BufferedStream] GRACEFUL DEGRADATION: Job={Job}, Segment={SegmentIndex}/{TotalSegments} (ID: {SegmentId}) failed after {MaxRetries} attempts. Substituting with zeros to allow stream to continue. Last error: {LastError}",
            jobName, index, segmentIds.Length, segmentId, maxRetries, lastException?.Message ?? "Unknown");

        // Track this segment as corrupted
        _corruptedSegments.Add((index, segmentId));

        // Report corruption back to database if we have a DavItemId
        if (_usageContext?.DetailsObject?.DavItemId != null)
        {
            var davItemId = _usageContext.Value.DetailsObject.DavItemId.Value;
            var reason = $"Data missing/corrupt after {maxRetries} retries: {lastException?.Message ?? "Unknown error"}";
            _ = Task.Run(async () => {
                try {
                    using var db = new DavDatabaseContext();
                    var item = await db.Items.FindAsync(davItemId);
                    if (item != null) {
                        item.IsCorrupted = true;
                        item.CorruptionReason = reason;
                        // Trigger immediate urgent health check (HEAD)
                        item.NextHealthCheck = DateTimeOffset.MinValue;
                        await db.SaveChangesAsync();
                        Log.Information("[BufferedStream] Marked item {ItemId} as corrupted and scheduled urgent health check due to terminal segment failure.", davItemId);
                    }
                } catch (Exception dbEx) {
                    Log.Error(dbEx, "[BufferedStream] Failed to mark item as corrupted in database.");
                }
            });
        }

        // Determine correct size for zero-filling
        int zeroBufferSize;
        if (_segmentSizes != null && index < _segmentSizes.Length)
        {
            zeroBufferSize = (int)_segmentSizes[index];
        }
        else if (_lastSuccessfulSegmentSize > 0 && index < segmentIds.Length - 1)
        {
            // Fallback: If not the last segment, assume it is the same size as the largest successful segment seen so far.
            // This is a safe bet for 99.9% of Usenet posts where segments are uniform size.
            // For the last segment, we cannot guess safely, so we still fail.
            zeroBufferSize = _lastSuccessfulSegmentSize;
            Log.Warning("[BufferedStream] Estimating segment size {Size} for segment {Index}/{Total} based on max observed segment size to allow graceful degradation.", 
                zeroBufferSize, index, segmentIds.Length);
        }
        else
        {
            // If we don't know the exact size, we cannot safely zero-fill without corrupting the file structure (shifting offsets).
            // It is safer to fail hard here.
            throw new InvalidDataException($"Cannot perform graceful degradation for segment {index} ({segmentId}) because segment size is unknown. Failing stream to prevent structural corruption.");
        }

        // Return a zero-filled segment of correct size
        var zeroBuffer = ArrayPool<byte>.Shared.Rent(zeroBufferSize);
        Array.Clear(zeroBuffer, 0, zeroBufferSize);
        return new PooledSegmentData(segmentId, zeroBuffer, zeroBufferSize);
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (count == 0) return 0;

        int totalRead = 0;

        while (totalRead < count && !cancellationToken.IsCancellationRequested)
        {
            // Get current segment if we don't have one
            if (_currentSegment == null)
            {
                if (!_bufferChannel.Reader.TryRead(out _currentSegment))
                {
                    // Update usage context while waiting to ensure UI shows we are waiting for next segment
                    UpdateUsageContext();

                    var waitWatch = Stopwatch.StartNew();
                    var hasData = await _bufferChannel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                    waitWatch.Stop();

                    if (EnableDetailedTiming)
                    {
                        Interlocked.Add(ref _totalChannelReadWaitMs, waitWatch.ElapsedMilliseconds);
                        Interlocked.Add(ref s_totalChannelReadWaitMs, waitWatch.ElapsedMilliseconds);
                    }

                    if (waitWatch.ElapsedMilliseconds > 50)
                    {
                        Log.Debug("[BufferedStream] Starvation: Waited {Duration}ms for next segment (Buffered: {Buffered}, Fetched: {Fetched}, Read: {Read})",
                            waitWatch.ElapsedMilliseconds, _bufferedCount, _totalFetchedCount, _totalReadCount);
                    }

                    if (!hasData)
                    {
                        break; // No more segments
                    }

                    if (!_bufferChannel.Reader.TryRead(out _currentSegment))
                    {
                        break;
                    }
                }

                _currentSegmentPosition = 0;
            }

            // Read from current segment
            var bytesAvailable = _currentSegment.Length - _currentSegmentPosition;
            if (bytesAvailable == 0)
            {
                _currentSegment.Dispose();
                _currentSegment = null;
                continue;
            }

            var bytesToRead = Math.Min(count - totalRead, bytesAvailable);
            Buffer.BlockCopy(_currentSegment.Data, _currentSegmentPosition, buffer, offset + totalRead, bytesToRead);

            _currentSegmentPosition += bytesToRead;
            totalRead += bytesToRead;
            _position += bytesToRead;

            // If segment is exhausted, move to next
            if (_currentSegmentPosition >= _currentSegment.Length)
            {
                _currentSegment.Dispose();
                _currentSegment = null;
                Interlocked.Decrement(ref _bufferedCount);
                Interlocked.Increment(ref _totalReadCount);
                if (EnableDetailedTiming) Interlocked.Increment(ref s_totalSegmentsRead);
                Interlocked.Increment(ref _nextIndexToRead);
            }
        }
        return totalRead;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length { get; }
    
    /// <summary>
    /// The date of the file on the Usenet server, populated when the first segment is fetched.
    /// </summary>
    public DateTimeOffset? FileDate { get; private set; }

    /// <summary>
    /// List of segments that failed CRC/size validation after all retries.
    /// Used to trigger health checks for files with corruption.
    /// </summary>
    public IReadOnlyCollection<(int Index, string SegmentId)> CorruptedSegments => _corruptedSegments.ToList();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException("Seeking is not supported.");
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException("Seeking is not supported.");
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            // Unregister stream from connection limiter
            StreamingConnectionLimiter.Instance?.UnregisterStream(_streamType);

            _cts.Cancel();
            _cts.Dispose();
            _bufferChannel.Writer.TryComplete();
            try { _fetchTask.Wait(TimeSpan.FromSeconds(5)); } catch { }

            _currentSegment?.Dispose();
            _currentSegment = null;

            // Dispose context scopes
            foreach (var scope in _contextScopes)
                scope?.Dispose();

            _linkedCts.Dispose();
        }
        _disposed = true;
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        // Unregister stream from connection limiter
        StreamingConnectionLimiter.Instance?.UnregisterStream(_streamType);

        _cts.Cancel();
        _bufferChannel.Writer.TryComplete();

        try
        {
            await _fetchTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch { }

        _cts.Dispose();

        _currentSegment?.Dispose();
        _currentSegment = null;

        // Dispose context scopes
        foreach (var scope in _contextScopes)
            scope?.Dispose();

        _linkedCts.Dispose();

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private class PooledSegmentData : IDisposable
    {
        private byte[]? _buffer;

        public string SegmentId { get; }
        public byte[] Data => _buffer ?? Array.Empty<byte>();
        public int Length { get; }

        public PooledSegmentData(string segmentId, byte[] buffer, int length)
        {
            SegmentId = segmentId;
            _buffer = buffer;
            Length = length;
        }

        public void Dispose()
        {
            if (_buffer != null)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = null;
            }
        }
    }

    private static MultiProviderNntpClient? GetMultiProviderClient(INntpClient client)
    {
        while (true)
        {
            if (client is MultiProviderNntpClient multiProviderClient) return multiProviderClient;
            if (client is WrappingNntpClient wrappingClient)
            {
                client = wrappingClient.InnerClient;
                continue;
            }
            return null;
        }
    }

    /// <summary>
    /// Merges two exclusion sets into a single HashSet.
    /// Returns null if both inputs are null or empty.
    /// </summary>
    private static HashSet<int>? MergeExclusions(HashSet<int>? initial, HashSet<int>? additional)
    {
        if ((initial == null || initial.Count == 0) && (additional == null || additional.Count == 0))
            return null;

        var result = new HashSet<int>();
        if (initial != null) result.UnionWith(initial);
        if (additional != null) result.UnionWith(additional);
        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Records a successful fetch for a provider, improving its score.
    /// </summary>
    private void RecordProviderSuccess(int providerIndex)
    {
        var score = _providerScores.GetOrAdd(providerIndex, _ => new ProviderStreamScore());
        score.RecordSuccess();
    }

    /// <summary>
    /// Records a straggler/failure for a provider, potentially putting it in cooldown.
    /// Uses rolling window for success rate and weighted failure tracking for cooldown duration.
    /// </summary>
    private void RecordProviderStraggler(int providerIndex, int totalProviders)
    {
        var score = _providerScores.GetOrAdd(providerIndex, _ => new ProviderStreamScore());
        score.RecordFailure();

        // Calculate cooldown based on failure weight (decays slowly) and rolling window failure rate
        // FailureWeight: each failure adds +2, each success subtracts -1 (min 0)
        // This makes failures "sticky" - they don't immediately reset on success
        var failureRate = 1.0 - score.SuccessRate;
        var failureWeight = score.FailureWeight;

        // AGGRESSIVE COOLDOWN FORMULA with sticky failure weight:
        // Base: 15s (was 10s)
        // + failureWeight * 3s - each failure adds 6s (2 weight * 3s), each success removes 3s
        // + failureRate * 20s - so 10% failure rate adds 2s, 50% adds 10s
        // Max: 60s (was 45s)
        //
        // Example cooldowns (assuming 30 ops in window):
        // - First failure (weight=2, ~3% rate): 15 + 6 + 0.6 = 21.6s
        // - Second failure before decay (weight=4): 15 + 12 + 1.2 = 28.2s
        // - After 1 success (weight=3): 15 + 9 + 1.0 = 25s (still significant!)
        // - After 5 failures, 3 successes (weight=7): 15 + 21 + 3 = 39s
        var cooldownSeconds = Math.Min(60, 15 + (failureWeight * 3) + (failureRate * 20));

        // Check how many providers are currently in cooldown
        var providersInCooldown = _providerScores.Count(kvp => kvp.Value.IsInCooldown);

        // Never put more than (totalProviders - 1) in cooldown at once
        if (providersInCooldown >= totalProviders - 1)
        {
            // This provider gets a shorter cooldown (5s) since others are already cooling
            // Still want some penalty, but can't fully deprioritize
            cooldownSeconds = 5;
            Log.Debug("[BufferedStream] Provider {Provider} limited to 5s cooldown - {InCooldown}/{Total} providers already cooling down",
                providerIndex, providersInCooldown, totalProviders);
        }

        score.CooldownUntil = DateTimeOffset.UtcNow.AddSeconds(cooldownSeconds);

        Log.Debug("[BufferedStream] Provider {Provider} enters {Cooldown:F1}s cooldown. Rolling window: {Rate:P0} ({Failures}/{Window}), FailureWeight: {Weight}",
            providerIndex, cooldownSeconds, score.SuccessRate, score.WindowFailures, score.WindowOperations, failureWeight);
    }

    /// <summary>
    /// Gets the set of providers currently in cooldown (for soft deprioritization, not exclusion).
    /// </summary>
    private HashSet<int> GetProvidersInCooldown()
    {
        var result = new HashSet<int>();
        foreach (var kvp in _providerScores)
        {
            if (kvp.Value.IsInCooldown)
            {
                result.Add(kvp.Key);
            }
        }
        return result;
    }

}
