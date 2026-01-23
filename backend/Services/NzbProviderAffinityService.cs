using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Tracks provider performance per NZB to optimize provider selection.
/// Records success rates and download speeds to prefer fast, reliable providers for each NZB.
/// Incorporates benchmark results with higher weight than per-segment speed.
/// </summary>
public class NzbProviderAffinityService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConfigManager _configManager;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, ProviderPerformance>> _stats = new();
    private readonly ConcurrentDictionary<int, BenchmarkSpeed> _benchmarkSpeeds = new();
    private readonly Timer _persistenceTimer;
    private readonly Timer _benchmarkRefreshTimer;
    private readonly SemaphoreSlim _dbWriteLock = new(1, 1);

    public NzbProviderAffinityService(
        IServiceScopeFactory scopeFactory,
        ConfigManager configManager)
    {
        _scopeFactory = scopeFactory;
        _configManager = configManager;

        // Persist stats every 5 seconds
        _persistenceTimer = new Timer(PersistStats, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        // Refresh benchmark speeds every 60 seconds (in case new benchmarks are run)
        _benchmarkRefreshTimer = new Timer(_ => _ = Task.Run(LoadBenchmarkSpeedsAsync), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));

        // Load existing stats and benchmark data from database
        _ = Task.Run(async () =>
        {
            await LoadStatsAsync().ConfigureAwait(false);
            await LoadBenchmarkSpeedsAsync().ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Force reload of benchmark speeds from database (called after new benchmark completes)
    /// </summary>
    public Task RefreshBenchmarkSpeeds() => LoadBenchmarkSpeedsAsync();

    /// <summary>
    /// Record a successful segment download with timing information
    /// </summary>
    public void RecordSuccess(string jobName, int providerIndex, long bytes, long elapsedMs)
    {
        if (!_configManager.IsProviderAffinityEnabled()) return;
        if (string.IsNullOrEmpty(jobName)) return;

        var jobStats = _stats.GetOrAdd(jobName, _ => new ConcurrentDictionary<int, ProviderPerformance>());
        var providerStats = jobStats.GetOrAdd(providerIndex, _ => new ProviderPerformance());

        providerStats.RecordSuccess(bytes, elapsedMs);
    }

    /// <summary>
    /// Record a failed segment download (generic failure)
    /// </summary>
    public void RecordFailure(string jobName, int providerIndex)
    {
        if (!_configManager.IsProviderAffinityEnabled()) return;
        if (string.IsNullOrEmpty(jobName)) return;

        var jobStats = _stats.GetOrAdd(jobName, _ => new ConcurrentDictionary<int, ProviderPerformance>());
        var providerStats = jobStats.GetOrAdd(providerIndex, _ => new ProviderPerformance());

        providerStats.RecordFailure();

        // Log straggler failures to help diagnose slow provider issues
        Log.Debug("[NzbProviderAffinity] RecordFailure: Job={JobName}, Provider={ProviderIndex}, TotalFailures={Failures}",
            jobName, providerIndex, providerStats.FailedSegments);
    }

    /// <summary>
    /// Record a timeout error for a segment fetch (includes in FailedSegments)
    /// </summary>
    public void RecordTimeoutError(string jobName, int providerIndex)
    {
        if (!_configManager.IsProviderAffinityEnabled()) return;
        if (string.IsNullOrEmpty(jobName)) return;

        var jobStats = _stats.GetOrAdd(jobName, _ => new ConcurrentDictionary<int, ProviderPerformance>());
        var providerStats = jobStats.GetOrAdd(providerIndex, _ => new ProviderPerformance());

        providerStats.RecordTimeoutError();

        Log.Debug("[NzbProviderAffinity] RecordTimeoutError: Job={JobName}, Provider={ProviderIndex}, TimeoutErrors={Timeouts}",
            jobName, providerIndex, providerStats.TimeoutErrors);
    }

    /// <summary>
    /// Record a missing article (430) error for a segment fetch (includes in FailedSegments)
    /// </summary>
    public void RecordMissingArticleError(string jobName, int providerIndex)
    {
        if (!_configManager.IsProviderAffinityEnabled()) return;
        if (string.IsNullOrEmpty(jobName)) return;

        var jobStats = _stats.GetOrAdd(jobName, _ => new ConcurrentDictionary<int, ProviderPerformance>());
        var providerStats = jobStats.GetOrAdd(providerIndex, _ => new ProviderPerformance());

        providerStats.RecordMissingArticleError();

        Log.Debug("[NzbProviderAffinity] RecordMissingArticleError: Job={JobName}, Provider={ProviderIndex}, MissingErrors={Missing}",
            jobName, providerIndex, providerStats.MissingArticleErrors);
    }

    /// <summary>
    /// Get the preferred provider index for an NZB based on performance history.
    /// Uses epsilon-greedy strategy: exploits best provider most of the time,
    /// but explores other providers 10% of the time to gather performance data.
    /// Returns null if no preference exists or affinity is disabled.
    ///
    /// When usageType is HealthCheck or Queue and there is active buffered streaming,
    /// prefers a non-fastest provider to give streaming priority on the fastest provider.
    /// </summary>
    public int? GetPreferredProvider(
        string jobName,
        int totalProviders = 0,
        bool logDecision = false,
        ConnectionUsageType? usageType = null)
    {
        if (!_configManager.IsProviderAffinityEnabled()) return null;
        if (string.IsNullOrEmpty(jobName)) return null;
        if (!_stats.TryGetValue(jobName, out var jobStats)) return null;

        // Get provider configuration for type filtering
        var providerConfig = _configManager.GetUsenetProviderConfig();

        // Check if we should defer to non-fastest provider for background operations
        var shouldDeferToStreaming = ShouldDeferToStreaming(usageType, providerConfig);

        // Epsilon-greedy exploration strategy: 10% exploration, 90% exploitation
        const double explorationRate = 0.10;
        var shouldExplore = Random.Shared.NextDouble() < explorationRate;

        if (shouldExplore && totalProviders > 0)
        {
            // Exploration: Choose a provider that hasn't been tested enough
            // Only explore Pooled and BackupAndStats providers, exclude BackupOnly providers
            var explorableProviders = Enumerable.Range(0, totalProviders)
                .Where(providerIndex =>
                {
                    // Check if provider should participate in exploration
                    if (providerIndex >= providerConfig.Providers.Count)
                        return false; // Provider index out of range

                    var providerType = providerConfig.Providers[providerIndex].Type;
                    // Only explore Pooled and BackupAndStats providers
                    return providerType == Models.ProviderType.Pooled ||
                           providerType == Models.ProviderType.BackupAndStats;
                })
                .Where(providerIndex => !jobStats.ContainsKey(providerIndex) || jobStats[providerIndex].SuccessfulSegments < 10)
                .ToList();

            if (explorableProviders.Count > 0)
            {
                var exploredProvider = explorableProviders[Random.Shared.Next(explorableProviders.Count)];
                return exploredProvider;
            }
        }

        // Exploitation: Use the best known provider
        // Require at least 10 successful segments before establishing a preference
        // Only consider Pooled and BackupAndStats providers, exclude BackupOnly
        const int minSuccessfulSegments = 10;

        var eligibleProviders = jobStats
            .Where(kvp => kvp.Value.SuccessfulSegments >= minSuccessfulSegments)
            .Where(kvp =>
            {
                // Check if provider should be considered for affinity preference
                if (kvp.Key >= providerConfig.Providers.Count)
                    return false; // Provider index out of range

                var providerType = providerConfig.Providers[kvp.Key].Type;
                // Only consider Pooled and BackupAndStats providers
                return providerType == Models.ProviderType.Pooled ||
                       providerType == Models.ProviderType.BackupAndStats;
            })
            .ToList();

        if (eligibleProviders.Count == 0) return null;

        // Find the maximum segment speed among all providers for normalization
        var maxSegmentSpeed = eligibleProviders.Max(kvp => kvp.Value.AverageSpeedBps);
        if (maxSegmentSpeed == 0) maxSegmentSpeed = 1; // Avoid division by zero

        // Find the maximum benchmark speed among providers that have benchmark data
        var maxBenchmarkSpeed = _benchmarkSpeeds.Values
            .Where(b => eligibleProviders.Any(p => p.Key == b.ProviderIndex))
            .Select(b => b.SpeedMbps)
            .DefaultIfEmpty(0)
            .Max();
        if (maxBenchmarkSpeed == 0) maxBenchmarkSpeed = 1; // Avoid division by zero

        // Check if any eligible provider has benchmark data
        var hasBenchmarkData = eligibleProviders.Any(p => _benchmarkSpeeds.ContainsKey(p.Key));

        var candidates = eligibleProviders
            .Select(kvp =>
            {
                var normalizedSuccessRate = kvp.Value.SuccessRate; // Already 0-100
                var normalizedSegmentSpeed = (kvp.Value.AverageSpeedBps / (double)maxSegmentSpeed) * 100.0;

                double score;
                if (hasBenchmarkData && _benchmarkSpeeds.TryGetValue(kvp.Key, out var benchmark))
                {
                    // With benchmark data:
                    // - Success rate: 40% weight (high priority - unreliable providers are unusable)
                    // - Benchmark speed: 35% weight (reliable speed measurement)
                    // - Segment speed: 25% weight (real-world performance for this specific NZB)
                    var normalizedBenchmarkSpeed = (benchmark.SpeedMbps / maxBenchmarkSpeed) * 100.0;
                    score = (normalizedSuccessRate * 0.40) +
                            (normalizedBenchmarkSpeed * 0.35) +
                            (normalizedSegmentSpeed * 0.25);
                }
                else if (hasBenchmarkData)
                {
                    // Provider has no benchmark but others do - penalize slightly
                    // Use segment speed only with reduced weight
                    score = (normalizedSuccessRate * 0.45) +
                            (normalizedSegmentSpeed * 0.35); // Max score of 80 without benchmark
                }
                else
                {
                    // No benchmark data available for any provider
                    // Success rate: 45% weight (reliability is crucial), Segment speed: 55% weight
                    score = (normalizedSuccessRate * 0.45) +
                            (normalizedSegmentSpeed * 0.55);
                }

                return new
                {
                    ProviderIndex = kvp.Key,
                    Stats = kvp.Value,
                    NormalizedSuccessRate = normalizedSuccessRate,
                    NormalizedSegmentSpeed = normalizedSegmentSpeed,
                    Score = score
                };
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        if (candidates.Count == 0) return null;

        // If we should defer to streaming, pick the second-best provider (if available)
        // This gives streaming priority on the fastest provider
        if (shouldDeferToStreaming && candidates.Count > 1)
        {
            var fastestProvider = candidates[0];
            var secondBest = candidates[1];

            if (logDecision)
            {
                Log.Debug("[NzbProviderAffinity] Background operation ({UsageType}) deferring to streaming: " +
                          "using provider {SecondBest} (score={SecondScore:F1}) instead of fastest {Fastest} (score={FastestScore:F1})",
                    usageType, secondBest.ProviderIndex, secondBest.Score, fastestProvider.ProviderIndex, fastestProvider.Score);
            }

            return secondBest.ProviderIndex;
        }

        return candidates[0].ProviderIndex;
    }

    /// <summary>
    /// Determines if operations should defer by choosing a non-fastest provider.
    /// Priority order: PlexPlayback > PlexBackground > BufferedStreaming > HealthCheck/Queue
    ///
    /// - PlexPlayback: Never defers (highest priority)
    /// - PlexBackground: Defers only to PlexPlayback
    /// - BufferedStreaming: Defers to both PlexPlayback and PlexBackground
    /// - HealthCheck/Queue: Defers to all streaming types
    /// </summary>
    private bool ShouldDeferToStreaming(ConnectionUsageType? usageType, UsenetProviderConfig providerConfig)
    {
        // PlexPlayback never defers - it has highest priority
        if (usageType == ConnectionUsageType.PlexPlayback)
            return false;

        // Only these types can defer
        if (usageType is not (ConnectionUsageType.HealthCheck or ConnectionUsageType.Queue or
            ConnectionUsageType.PlexBackground or ConnectionUsageType.BufferedStreaming))
            return false;

        var limiter = StreamingConnectionLimiter.Instance;
        var activeRealPlayback = limiter?.ActiveRealPlaybackStreams ?? 0;
        var activePlexBackground = limiter?.ActivePlexBackgroundStreams ?? 0;

        // Determine what this usage type should defer to
        bool shouldDefer;
        string deferReason;

        if (usageType == ConnectionUsageType.BufferedStreaming)
        {
            // BufferedStreaming defers to both PlexPlayback AND PlexBackground
            shouldDefer = activeRealPlayback > 0 || activePlexBackground > 0;
            deferReason = $"{activeRealPlayback} PlexPlayback + {activePlexBackground} PlexBackground";
        }
        else
        {
            // PlexBackground, HealthCheck, Queue only defer to PlexPlayback
            shouldDefer = activeRealPlayback > 0;
            deferReason = $"{activeRealPlayback} PlexPlayback";
        }

        if (!shouldDefer)
            return false;

        // Check if there are multiple pooled providers (need at least 2 to defer)
        var pooledProviderCount = providerConfig.Providers.Count(p =>
            p.Type == Models.ProviderType.Pooled || p.Type == Models.ProviderType.BackupAndStats);

        if (pooledProviderCount < 2)
            return false;

        Log.Debug("[NzbProviderAffinity] Deferring {UsageType} to non-fastest provider: {DeferReason} active, {PooledCount} pooled providers",
            usageType, deferReason, pooledProviderCount);

        return true;
    }

    /// <summary>
    /// Get providers with low success rate (< threshold%) for a specific job.
    /// Requires minimum sample size to avoid penalizing providers with insufficient data.
    /// </summary>
    /// <param name="jobName">The NZB/job name (affinity key)</param>
    /// <param name="minSamples">Minimum total segments (success + failures) required</param>
    /// <param name="successRateThreshold">Success rate below this is considered "low" (default 30%)</param>
    /// <returns>Set of provider indices with low success rate</returns>
    public HashSet<int> GetLowSuccessRateProviders(string jobName, int minSamples = 10, double successRateThreshold = 30.0)
    {
        var result = new HashSet<int>();

        if (string.IsNullOrEmpty(jobName)) return result;
        if (!_stats.TryGetValue(jobName, out var jobStats)) return result;

        foreach (var (providerIndex, performance) in jobStats)
        {
            var totalSegments = performance.SuccessfulSegments + performance.FailedSegments;

            // Skip providers without enough data
            if (totalSegments < minSamples) continue;

            // Check if success rate is below threshold
            if (performance.SuccessRate < successRateThreshold)
            {
                result.Add(providerIndex);
            }
        }

        return result;
    }

    /// <summary>
    /// Get all provider statistics for a specific NZB
    /// </summary>
    public Dictionary<int, NzbProviderStats> GetJobStats(string jobName)
    {
        if (!_stats.TryGetValue(jobName, out var jobStats))
            return new Dictionary<int, NzbProviderStats>();

        var result = new Dictionary<int, NzbProviderStats>();
        foreach (var (providerIndex, performance) in jobStats)
        {
            var stats = new NzbProviderStats
            {
                JobName = jobName,
                ProviderIndex = providerIndex
            };
            performance.CopyTo(stats);
            result[providerIndex] = stats;
        }
        return result;
    }

    /// <summary>
    /// Clear statistics for a specific job (when queue item is deleted)
    /// </summary>
    public void ClearJobStats(string jobName)
    {
        _stats.TryRemove(jobName, out _);
    }

    /// <summary>
    /// Clear all in-memory statistics (when all stats are reset via API)
    /// </summary>
    public void ClearAllStats()
    {
        _stats.Clear();
    }

    private async Task LoadStatsAsync()
    {
        try
        {
            await _dbWriteLock.WaitAsync();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

                var allStats = await dbContext.NzbProviderStats
                    .AsNoTracking()
                    .ToListAsync()
                    .ConfigureAwait(false);

                foreach (var stat in allStats)
                {
                    var jobStats = _stats.GetOrAdd(stat.JobName, _ => new ConcurrentDictionary<int, ProviderPerformance>());
                    var providerStats = jobStats.GetOrAdd(stat.ProviderIndex, _ => new ProviderPerformance());

                    providerStats.LoadFromDb(stat);
                }
            }
            finally
            {
                _dbWriteLock.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NzbProviderAffinity] Failed to load stats from database");
        }
    }

    private async void PersistStats(object? state)
    {
        if (!_configManager.IsProviderAffinityEnabled()) return;

        try
        {
            await _dbWriteLock.WaitAsync();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

                var now = DateTimeOffset.UtcNow;
                var hasChanges = false;

                foreach (var (jobName, jobStats) in _stats)
                {
                    foreach (var (providerIndex, performance) in jobStats)
                    {
                        if (!performance.IsDirty) continue;

                        var dbStats = await dbContext.NzbProviderStats
                            .FindAsync(jobName, providerIndex)
                            .ConfigureAwait(false);

                        if (dbStats == null)
                        {
                            dbStats = new NzbProviderStats
                            {
                                JobName = jobName,
                                ProviderIndex = providerIndex
                            };
                            dbContext.NzbProviderStats.Add(dbStats);
                        }

                        performance.SaveToDb(dbStats, now);
                        hasChanges = true;
                    }
                }

                if (hasChanges)
                {
                    await dbContext.SaveChangesAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _dbWriteLock.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NzbProviderAffinity] Failed to persist stats to database");
        }
    }

    private class ProviderPerformance
    {
        private readonly object _lock = new();
        private int _successfulSegments;
        private int _failedSegments;
        private int _timeoutErrors;
        private int _missingArticleErrors;
        private long _totalBytes;
        private long _totalTimeMs;
        private long _recentAverageSpeedBps;
        private bool _isDirty;

        // EWMA smoothing factor: 0.15 means 15% weight to new data, 85% to existing average
        private const double Alpha = 0.15;
        // Outlier rejection: reject speeds outside of [0.1x, 10x] of current average
        private const double OutlierMinFactor = 0.1;
        private const double OutlierMaxFactor = 10.0;

        public int SuccessfulSegments => _successfulSegments;
        public int FailedSegments => _failedSegments;
        public int TimeoutErrors => _timeoutErrors;
        public int MissingArticleErrors => _missingArticleErrors;
        public long TotalBytes => _totalBytes;
        public long TotalTimeMs => _totalTimeMs;
        public long RecentAverageSpeedBps => _recentAverageSpeedBps;
        public bool IsDirty => _isDirty;

        public double SuccessRate
        {
            get
            {
                var total = _successfulSegments + _failedSegments;
                return total > 0 ? (_successfulSegments * 100.0) / total : 0;
            }
        }

        public long AverageSpeedBps
        {
            get
            {
                return _totalTimeMs > 0 ? (_totalBytes * 1000) / _totalTimeMs : 0;
            }
        }

        public void RecordSuccess(long bytes, long elapsedMs)
        {
            lock (_lock)
            {
                _successfulSegments++;
                _totalBytes += bytes;
                _totalTimeMs += elapsedMs;

                // Calculate current segment speed
                var currentSpeed = elapsedMs > 0 ? (bytes * 1000) / elapsedMs : 0;

                // Initialize EWMA on first segment or apply outlier rejection
                if (_recentAverageSpeedBps == 0)
                {
                    _recentAverageSpeedBps = currentSpeed;
                }
                else
                {
                    // Check if current speed is an outlier
                    var minSpeed = (long)(_recentAverageSpeedBps * OutlierMinFactor);
                    var maxSpeed = (long)(_recentAverageSpeedBps * OutlierMaxFactor);

                    if (currentSpeed >= minSpeed && currentSpeed <= maxSpeed)
                    {
                        // Apply EWMA: new_avg = alpha * current + (1-alpha) * old_avg
                        _recentAverageSpeedBps = (long)(Alpha * currentSpeed + (1 - Alpha) * _recentAverageSpeedBps);
                    }
                    // else: reject outlier, keep existing average
                }

                _isDirty = true;
            }
        }

        public void RecordFailure()
        {
            lock (_lock)
            {
                _failedSegments++;
                _isDirty = true;
            }
        }

        public void RecordTimeoutError()
        {
            lock (_lock)
            {
                _failedSegments++;
                _timeoutErrors++;
                _isDirty = true;
            }
        }

        public void RecordMissingArticleError()
        {
            lock (_lock)
            {
                _failedSegments++;
                _missingArticleErrors++;
                _isDirty = true;
            }
        }

        public void LoadFromDb(NzbProviderStats dbStats)
        {
            lock (_lock)
            {
                _successfulSegments = dbStats.SuccessfulSegments;
                _failedSegments = dbStats.FailedSegments;
                _timeoutErrors = dbStats.TimeoutErrors;
                _missingArticleErrors = dbStats.MissingArticleErrors;
                _totalBytes = dbStats.TotalBytes;
                _totalTimeMs = dbStats.TotalTimeMs;
                _recentAverageSpeedBps = dbStats.RecentAverageSpeedBps;
                _isDirty = false;
            }
        }

        public void CopyTo(NzbProviderStats dbStats)
        {
            lock (_lock)
            {
                dbStats.SuccessfulSegments = _successfulSegments;
                dbStats.FailedSegments = _failedSegments;
                dbStats.TimeoutErrors = _timeoutErrors;
                dbStats.MissingArticleErrors = _missingArticleErrors;
                dbStats.TotalBytes = _totalBytes;
                dbStats.TotalTimeMs = _totalTimeMs;
                dbStats.RecentAverageSpeedBps = _recentAverageSpeedBps;
                dbStats.LastUsed = DateTimeOffset.UtcNow;
            }
        }

        public void SaveToDb(NzbProviderStats dbStats, DateTimeOffset now)
        {
            lock (_lock)
            {
                dbStats.SuccessfulSegments = _successfulSegments;
                dbStats.FailedSegments = _failedSegments;
                dbStats.TimeoutErrors = _timeoutErrors;
                dbStats.MissingArticleErrors = _missingArticleErrors;
                dbStats.TotalBytes = _totalBytes;
                dbStats.TotalTimeMs = _totalTimeMs;
                dbStats.RecentAverageSpeedBps = _recentAverageSpeedBps;
                dbStats.LastUsed = now;
                _isDirty = false;
            }
        }
    }

    /// <summary>
    /// Cached benchmark speed data for a provider
    /// </summary>
    private record BenchmarkSpeed(int ProviderIndex, string ProviderHost, double SpeedMbps, DateTimeOffset CreatedAt);

    /// <summary>
    /// Load the latest successful benchmark speed for each provider
    /// </summary>
    private async Task LoadBenchmarkSpeedsAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

            // Get the most recent successful benchmark result for each provider (non-load-balanced only)
            var latestBenchmarks = await dbContext.ProviderBenchmarkResults
                .AsNoTracking()
                .Where(r => r.Success && !r.IsLoadBalanced && r.ProviderIndex >= 0)
                .GroupBy(r => r.ProviderIndex)
                .Select(g => g.OrderByDescending(r => r.CreatedAt).First())
                .ToListAsync()
                .ConfigureAwait(false);

            // Update the cache
            _benchmarkSpeeds.Clear();
            foreach (var benchmark in latestBenchmarks)
            {
                _benchmarkSpeeds[benchmark.ProviderIndex] = new BenchmarkSpeed(
                    benchmark.ProviderIndex,
                    benchmark.ProviderHost,
                    benchmark.SpeedMbps,
                    benchmark.CreatedAt
                );
            }

            if (latestBenchmarks.Count > 0)
            {
                Log.Debug("[NzbProviderAffinity] Loaded {Count} benchmark speeds: {Speeds}",
                    latestBenchmarks.Count,
                    string.Join(", ", latestBenchmarks.Select(b => $"{b.ProviderHost}={b.SpeedMbps:F1}MB/s")));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NzbProviderAffinity] Failed to load benchmark speeds from database");
        }
    }
}
