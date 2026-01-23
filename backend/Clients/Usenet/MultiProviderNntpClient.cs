using System.Diagnostics;
using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using Serilog;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;
using Usenet.Nzb;

namespace NzbWebDAV.Clients.Usenet;

public class MultiProviderNntpClient : INntpClient
{
    public IReadOnlyList<MultiConnectionNntpClient> Providers { get; }
    private readonly ProviderErrorService? _providerErrorService;
    private readonly NzbProviderAffinityService? _affinityService;

    /// <summary>
    /// Exposes the affinity service for external callers (e.g., BufferedSegmentStream straggler detection)
    /// </summary>
    public NzbProviderAffinityService? AffinityService => _affinityService;

    public MultiProviderNntpClient(
        List<MultiConnectionNntpClient> providers,
        ProviderErrorService? providerErrorService = null,
        NzbProviderAffinityService? affinityService = null)
    {
        Providers = providers;
        _providerErrorService = providerErrorService;
        _affinityService = affinityService;
    }

    public Task<bool> ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Please connect within the connectionFactory");
    }

    public Task<bool> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Please authenticate within the connectionFactory");
    }

    public Task<UsenetStatResponse> StatAsync(string segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.StatAsync(segmentId, cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider, cancellationToken),
            cancellationToken,
            segmentId,
            "STAT");
    }

    public Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.DateAsync(cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider, cancellationToken),
            cancellationToken);
    }

    public Task<UsenetArticleHeaders> GetArticleHeadersAsync(string segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.GetArticleHeadersAsync(segmentId, cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider, cancellationToken),
            cancellationToken,
            segmentId,
            "HEADER");
    }

    public Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, bool includeHeaders,
        CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.GetSegmentStreamAsync(segmentId, includeHeaders, cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider, cancellationToken),
            cancellationToken,
            segmentId,
            "BODY");
    }

    public Task<YencHeaderStream> GetBalancedSegmentStreamAsync(string segmentId, bool includeHeaders,
        CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.GetSegmentStreamAsync(segmentId, includeHeaders, cancellationToken),
            GetBalancedProviders(cancellationToken),
            cancellationToken,
            segmentId,
            "BODY");
    }

    /// <summary>
    /// Fetches a segment from a specific provider by index, with fallback to other providers on failure.
    /// Used by batch segment assignment to reduce connection pool contention.
    /// </summary>
    public Task<YencHeaderStream> GetSegmentStreamFromProviderAsync(
        string segmentId,
        bool includeHeaders,
        int preferredProviderIndex,
        CancellationToken cancellationToken)
    {
        // Build provider list with preferred provider first, then others as fallback
        var preferredProvider = preferredProviderIndex >= 0 && preferredProviderIndex < Providers.Count
            ? Providers[preferredProviderIndex]
            : null;

        var orderedProviders = GetBalancedProviders(cancellationToken)
            .Prepend(preferredProvider)
            .Where(x => x is not null)
            .Select(x => x!)
            .Distinct();

        return RunFromPoolWithBackup(
            connection => connection.GetSegmentStreamAsync(segmentId, includeHeaders, cancellationToken),
            orderedProviders,
            cancellationToken,
            segmentId,
            "BODY");
    }

    /// <summary>
    /// Gets the list of pooled providers with their available connection counts.
    /// Used by batch segment assignment to distribute segments proportionally.
    /// </summary>
    public IReadOnlyList<(int ProviderIndex, int MaxConnections, int AvailableConnections)> GetPooledProviderCapacities()
    {
        return Providers
            .Where(p => p.ProviderType == ProviderType.Pooled)
            .Select(p => (p.ProviderIndex, p.ConnectionPool.MaxConnections, p.AvailableConnections))
            .ToList();
    }

    public Task<UsenetYencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.GetSegmentYencHeaderAsync(segmentId, cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider, cancellationToken),
            cancellationToken,
            segmentId,
            "BODY");
    }

    public Task<long> GetFileSizeAsync(NzbFile file, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.GetFileSizeAsync(file, cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider, cancellationToken),
            cancellationToken,
            null,
            "STAT");
    }

    public Task WaitForReady(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task<UsenetGroupResponse> GroupAsync(string group, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.GroupAsync(group, cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider, cancellationToken),
            cancellationToken);
    }

    public Task<long> DownloadArticleBodyAsync(string group, long articleId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.DownloadArticleBodyAsync(group, articleId, cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider, cancellationToken),
            cancellationToken,
            null,
            "BODY");
    }

    private async Task<T> RunFromPoolWithBackup<T>
    (
        Func<INntpClient, Task<T>> task,
        IEnumerable<MultiConnectionNntpClient> orderedProviders,
        CancellationToken cancellationToken,
        string? segmentId = null,
        string operationName = "UNKNOWN"
    )
    {
        ExceptionDispatchInfo? lastException = null;
        var lastSuccessfulProviderContext = cancellationToken.GetContext<LastSuccessfulProviderContext>();
        var lastSuccessfulProvider = lastSuccessfulProviderContext?.Provider;
        T? result = default;
        int attempts = 0;

        foreach (var provider in orderedProviders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ctx = cancellationToken.GetContext<ConnectionUsageContext>();
            if (ctx.DetailsObject != null)
            {
                // Track which provider we're currently using (for straggler detection)
                ctx.DetailsObject.CurrentProviderIndex = provider.ProviderIndex;

                if (provider.ProviderType != ProviderType.Pooled)
                {
                    ctx.DetailsObject.IsBackup = true;
                    ctx.DetailsObject.IsSecondary = false;
                }
                else if (attempts > 0)
                {
                    ctx.DetailsObject.IsSecondary = true;
                    ctx.DetailsObject.IsBackup = false;
                }
                else
                {
                    ctx.DetailsObject.IsSecondary = false;
                    ctx.DetailsObject.IsBackup = false;
                }
            }
            attempts++;

            if (lastException is not null && lastException.SourceException is not UsenetArticleNotFoundException)
            {
                var msg = lastException.SourceException.Message;
                Log.Debug("Encountered error during NNTP Operation: {Message}. Trying another provider.", msg);
            }

            // Track timing for provider affinity
            var stopwatch = Stopwatch.StartNew();
            try
            {
                result = await task.Invoke(provider).ConfigureAwait(false);
                stopwatch.Stop();

                if (result is UsenetStatResponse r && !r.ArticleExists)
                {
                    throw new UsenetArticleNotFoundException(r.ResponseMessage ?? segmentId ?? "Unknown");
                }

                // Record successful download for provider affinity
                if (_affinityService != null && operationName == "BODY")
                {
                    var affinityKey = ctx.AffinityKey;
                    if (!string.IsNullOrEmpty(affinityKey))
                    {
                        // Estimate bytes from result
                        long bytes = 0;
                        if (result is YencHeaderStream stream) bytes = stream.Length;
                        else if (result is UsenetYencHeader header) bytes = header.PartSize;
                        else if (result is long l) bytes = l;

                        if (bytes == 0)
                        {
                            Log.Debug("[MultiProvider] Zero bytes recorded for BODY operation. ResultType={Type}, AffinityKey={Key}", result?.GetType().Name ?? "null", affinityKey);
                        }

                        _affinityService.RecordSuccess(affinityKey, provider.ProviderIndex, bytes, stopwatch.ElapsedMilliseconds);
                    }
                }

                if (lastSuccessfulProviderContext is not null && lastSuccessfulProvider != provider)
                    lastSuccessfulProviderContext.Provider = provider;
                return result;
            }
            catch (UsenetArticleNotFoundException e)
            {
                stopwatch.Stop();

                // Explicitly caught to record it
                RecordMissingArticle(provider.ProviderIndex, e.SegmentId, cancellationToken, operationName);

                // Record missing article error for provider affinity (430 - article not found)
                if (_affinityService != null)
                {
                    var affinityKey = ctx.AffinityKey;
                    if (!string.IsNullOrEmpty(affinityKey))
                    {
                        _affinityService.RecordMissingArticleError(affinityKey, provider.ProviderIndex);
                    }
                }

                lastException = ExceptionDispatchInfo.Capture(e);
            }
            catch (Exception e) when (e is not OperationCanceledException and not TaskCanceledException)
            {
                stopwatch.Stop();

                // Record failure for provider affinity
                if (_affinityService != null)
                {
                    var affinityKey = ctx.AffinityKey;
                    if (!string.IsNullOrEmpty(affinityKey))
                    {
                        // Categorize the failure type for better diagnostics
                        // TimeoutException (explicit) and related network timeouts should be counted as timeout errors
                        if (e is TimeoutException || e.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
                        {
                            _affinityService.RecordTimeoutError(affinityKey, provider.ProviderIndex);
                        }
                        else
                        {
                            _affinityService.RecordFailure(affinityKey, provider.ProviderIndex);
                        }
                    }
                }

                // Don't overwrite UsenetArticleNotFoundException - it's more specific than generic errors
                if (lastException?.SourceException is not UsenetArticleNotFoundException)
                    lastException = ExceptionDispatchInfo.Capture(e);
            }
            catch (OperationCanceledException ex)
            {
                stopwatch.Stop();

                // Record timeout as specific error type for provider affinity (only if it's a real timeout, not parent cancellation)
                if (_affinityService != null && !cancellationToken.IsCancellationRequested)
                {
                    var affinityKey = ctx.AffinityKey;
                    if (!string.IsNullOrEmpty(affinityKey))
                    {
                        _affinityService.RecordTimeoutError(affinityKey, provider.ProviderIndex);
                    }
                }

                // If parent cancellation is requested, stop everything immediately
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                // Otherwise, this was a provider-specific timeout (e.g. from GetDynamicTimeout in MultiConnectionNntpClient)
                // We treat this as a transient failure and try the next provider.
                var elapsed = stopwatch.Elapsed.TotalSeconds;
                Log.Debug("Operation timed out on provider {Host} after {Elapsed:F2}s. Trying another provider.", provider.Host, elapsed);

                // Don't overwrite UsenetArticleNotFoundException with timeout - article not found is more specific
                // This ensures that if any provider definitively said "article not found", we report that
                // rather than a timeout from another provider
                if (lastException?.SourceException is not UsenetArticleNotFoundException)
                    lastException = ExceptionDispatchInfo.Capture(ex);
            }
        }

        if (result is UsenetStatResponse)
            return result;

        lastException?.Throw();
        throw new Exception("There are no usenet providers configured.");
    }

    private void RecordMissingArticle(int providerIndex, string segmentId, CancellationToken ct, string operation)
    {
        var context = ct.GetContext<ConnectionUsageContext>();
        var filename = context.DetailsObject?.Text ?? context.Details ?? "Unknown";

        var providerHost = providerIndex >= 0 && providerIndex < Providers.Count
            ? Providers[providerIndex].Host
            : $"Provider {providerIndex}";

        if (_providerErrorService == null) return;
        _providerErrorService.RecordError(providerIndex, filename, segmentId ?? "", "Article not found", context.IsImported, operation);
    }

    private IEnumerable<MultiConnectionNntpClient> GetOrderedProviders(MultiConnectionNntpClient? preferredProvider, CancellationToken cancellationToken)
    {
        // Check for forced provider (used for testing individual provider performance)
        var context = cancellationToken.GetContext<ConnectionUsageContext>();
        var forcedIndex = context.DetailsObject?.ForcedProviderIndex;
        if (forcedIndex.HasValue && forcedIndex.Value >= 0 && forcedIndex.Value < Providers.Count)
        {
            // Return ONLY the forced provider - no fallback, no affinity
            return new[] { Providers[forcedIndex.Value] };
        }

        // Get excluded providers (from straggler retry logic)
        // Read from struct first (per-operation, no race condition), then fall back to DetailsObject
        var excludedIndices = context.ExcludedProviderIndices ?? context.DetailsObject?.ExcludedProviderIndices;
        var hasExclusions = excludedIndices != null && excludedIndices.Count > 0;

        // Get providers with low success rate (< 30%) for this job - they become last resort
        HashSet<int>? lowSuccessRateProviders = null;
        if (_affinityService != null)
        {
            var affinityKey = context.AffinityKey;
            if (!string.IsNullOrEmpty(affinityKey))
            {
                lowSuccessRateProviders = _affinityService.GetLowSuccessRateProviders(affinityKey);
            }
        }
        var hasLowSuccessRate = lowSuccessRateProviders != null && lowSuccessRateProviders.Count > 0;

        // Check for NZB-level provider affinity with epsilon-greedy exploration
        MultiConnectionNntpClient? affinityProvider = null;
        if (_affinityService != null)
        {
            var affinityKey = context.AffinityKey;

            if (!string.IsNullOrEmpty(affinityKey))
            {
                // Only log affinity decisions for buffer streaming operations
                var logDecision = context.UsageType == ConnectionUsageType.BufferedStreaming;
                var preferredIndex = _affinityService.GetPreferredProvider(affinityKey, Providers.Count, logDecision, context.UsageType);
                if (preferredIndex.HasValue && preferredIndex.Value >= 0 && preferredIndex.Value < Providers.Count)
                {
                    // Skip affinity provider if it's in the excluded list or has low success rate
                    var isExcluded = hasExclusions && excludedIndices!.Contains(preferredIndex.Value);
                    var isLowSuccessRate = hasLowSuccessRate && lowSuccessRateProviders!.Contains(preferredIndex.Value);
                    if (!isExcluded && !isLowSuccessRate)
                    {
                        affinityProvider = Providers[preferredIndex.Value];
                    }
                }
            }
        }

        // Skip preferred provider if it's excluded or has low success rate
        if (preferredProvider != null)
        {
            var isExcluded = hasExclusions && excludedIndices!.Contains(preferredProvider.ProviderIndex);
            var isLowSuccessRate = hasLowSuccessRate && lowSuccessRateProviders!.Contains(preferredProvider.ProviderIndex);
            if (isExcluded || isLowSuccessRate)
            {
                preferredProvider = null;
            }
        }

        // Non-excluded, non-low-success-rate providers first
        var nonExcluded = Providers
            .Where(x => x.ProviderType != ProviderType.Disabled)
            .Where(x => !hasExclusions || !excludedIndices!.Contains(x.ProviderIndex))
            .Where(x => !hasLowSuccessRate || !lowSuccessRateProviders!.Contains(x.ProviderIndex))
            .OrderBy(x => x.ProviderType)
            .ThenByDescending(x => x.IdleConnections)
            .ThenByDescending(x => x.RemainingSemaphoreSlots);

        // Low success rate providers (< 30%) as last resort
        var lowSuccessRate = hasLowSuccessRate
            ? Providers
                .Where(x => x.ProviderType != ProviderType.Disabled && lowSuccessRateProviders!.Contains(x.ProviderIndex))
                .Where(x => !hasExclusions || !excludedIndices!.Contains(x.ProviderIndex)) // Not also excluded
                .OrderBy(x => x.ProviderType)
            : Enumerable.Empty<MultiConnectionNntpClient>();

        // Excluded providers are NOT included - if they already failed/timed out for this segment,
        // retrying them immediately is unlikely to succeed and just wastes time

        return nonExcluded.Concat(lowSuccessRate)
            .Prepend(preferredProvider)     // Stream-level stickiness (if not excluded or low success rate)
            .Prepend(affinityProvider)      // NZB-level affinity (HIGHEST priority - if not excluded or low success rate)
            .Where(x => x is not null)
            .Select(x => x!)
            .Distinct();
    }

    private IEnumerable<MultiConnectionNntpClient> GetBalancedProviders(CancellationToken cancellationToken = default)
    {
        // Check for forced provider (used for testing individual provider performance)
        var context = cancellationToken.GetContext<ConnectionUsageContext>();
        var forcedIndex = context.DetailsObject?.ForcedProviderIndex;
        if (forcedIndex.HasValue && forcedIndex.Value >= 0 && forcedIndex.Value < Providers.Count)
        {
            // Return ONLY the forced provider - no fallback, no affinity
            return new[] { Providers[forcedIndex.Value] };
        }

        // Get excluded providers (from straggler retry logic)
        // Read from struct first (per-operation, no race condition), then fall back to DetailsObject
        var excludedIndices = context.ExcludedProviderIndices ?? context.DetailsObject?.ExcludedProviderIndices;
        var hasExclusions = excludedIndices != null && excludedIndices.Count > 0;

        // Get deprioritized providers (from per-stream cooldown system)
        // These are used as last resort but not fully excluded
        var deprioritizedIndices = context.DeprioritizedProviderIndices;
        var hasDeprioritized = deprioritizedIndices != null && deprioritizedIndices.Count > 0;

        // Get providers with low success rate (< 30%) for this job - they become last resort
        HashSet<int>? lowSuccessRateProviders = null;
        if (_affinityService != null)
        {
            var affinityKey = context.AffinityKey;
            if (!string.IsNullOrEmpty(affinityKey))
            {
                lowSuccessRateProviders = _affinityService.GetLowSuccessRateProviders(affinityKey);
            }
        }
        var hasLowSuccessRate = lowSuccessRateProviders != null && lowSuccessRateProviders.Count > 0;

        // Balanced strategy for BufferedStream with NZB-level affinity support:
        // 1. Prioritize affinity provider (learned performance) - unless excluded or low success rate
        // 2. Then Pooled providers sorted by available connections and latency (excluding low success rate)
        // 3. Fallback to Backups (excluding low success rate)
        // 4. Low success rate providers (< 30%) as last resort before excluded
        // 5. Last resort: excluded providers (in case all others fail)

        // Check for NZB-level provider affinity
        MultiConnectionNntpClient? affinityProvider = null;
        if (_affinityService != null)
        {
            var affinityKey = context.AffinityKey;

            if (!string.IsNullOrEmpty(affinityKey))
            {
                // Log affinity decisions for buffer streaming
                var logDecision = context.UsageType == ConnectionUsageType.BufferedStreaming;
                var preferredIndex = _affinityService.GetPreferredProvider(affinityKey, Providers.Count, logDecision, context.UsageType);
                if (preferredIndex.HasValue && preferredIndex.Value >= 0 && preferredIndex.Value < Providers.Count)
                {
                    // Skip affinity provider if it's in the excluded list or has low success rate
                    var isExcluded = hasExclusions && excludedIndices!.Contains(preferredIndex.Value);
                    var isLowSuccessRate = hasLowSuccessRate && lowSuccessRateProviders!.Contains(preferredIndex.Value);
                    if (!isExcluded && !isLowSuccessRate)
                    {
                        affinityProvider = Providers[preferredIndex.Value];
                    }
                }
            }
        }

        // Build list of non-excluded, non-low-success-rate, non-deprioritized pooled providers
        var pooled = Providers
            .Where(x => x.ProviderType == ProviderType.Pooled)
            .Where(x => !hasExclusions || !excludedIndices!.Contains(x.ProviderIndex))
            .Where(x => !hasLowSuccessRate || !lowSuccessRateProviders!.Contains(x.ProviderIndex))
            .Where(x => !hasDeprioritized || !deprioritizedIndices!.Contains(x.ProviderIndex))
            .Select(x => new {
                Provider = x,
                // Calculate availability ratio (0.0 to 1.0) for better load distribution
                // This prevents thundering herd where all workers pick the same "available" provider
                AvailabilityRatio = x.ConnectionPool.MaxConnections > 0
                    ? (double)x.AvailableConnections / x.ConnectionPool.MaxConnections
                    : 0.0
            })
            .OrderByDescending(x => x.AvailabilityRatio > 0) // Has any availability
            .ThenByDescending(x => x.AvailabilityRatio)       // Prefer higher availability ratio
            .ThenBy(x => x.Provider.AverageLatency)           // Then by latency
            .Select(x => x.Provider)
            .ToList();

        // Non-excluded, non-low-success-rate, non-deprioritized backup providers
        var others = Providers
            .Where(x => x.ProviderType != ProviderType.Pooled && x.ProviderType != ProviderType.Disabled)
            .Where(x => !hasExclusions || !excludedIndices!.Contains(x.ProviderIndex))
            .Where(x => !hasLowSuccessRate || !lowSuccessRateProviders!.Contains(x.ProviderIndex))
            .Where(x => !hasDeprioritized || !deprioritizedIndices!.Contains(x.ProviderIndex))
            .OrderBy(x => x.ProviderType) // Backup vs BackupOnly
            .ThenByDescending(x => x.IdleConnections);

        // Deprioritized providers (per-stream cooldown) - use before low success rate
        var deprioritized = hasDeprioritized
            ? Providers
                .Where(x => x.ProviderType != ProviderType.Disabled && deprioritizedIndices!.Contains(x.ProviderIndex))
                .Where(x => !hasExclusions || !excludedIndices!.Contains(x.ProviderIndex)) // Not also excluded
                .Where(x => !hasLowSuccessRate || !lowSuccessRateProviders!.Contains(x.ProviderIndex)) // Not also low success rate
                .OrderBy(x => x.ProviderType)
            : Enumerable.Empty<MultiConnectionNntpClient>();

        // Low success rate providers (< 30%) as last resort
        var lowSuccessRate = hasLowSuccessRate
            ? Providers
                .Where(x => x.ProviderType != ProviderType.Disabled && lowSuccessRateProviders!.Contains(x.ProviderIndex))
                .Where(x => !hasExclusions || !excludedIndices!.Contains(x.ProviderIndex)) // Not also excluded
                .OrderBy(x => x.ProviderType)
            : Enumerable.Empty<MultiConnectionNntpClient>();

        // Excluded providers are NOT included - if they already failed/timed out for this segment,
        // retrying them immediately is unlikely to succeed and just wastes time

        if (hasExclusions)
        {
            Log.Debug("[MultiProvider] Excluding {Count} provider(s) from selection (will not retry): [{Indices}]",
                excludedIndices!.Count, string.Join(", ", excludedIndices));
        }

        if (hasDeprioritized)
        {
            Log.Debug("[MultiProvider] Deprioritizing {Count} provider(s) in cooldown: [{Indices}]",
                deprioritizedIndices!.Count, string.Join(", ", deprioritizedIndices));
        }

        if (hasLowSuccessRate)
        {
            Log.Debug("[MultiProvider] Low success rate (<30%) {Count} provider(s): [{Indices}]",
                lowSuccessRateProviders!.Count, string.Join(", ", lowSuccessRateProviders));
        }

        return pooled.Concat(others).Concat(deprioritized).Concat(lowSuccessRate)
            .Prepend(affinityProvider)  // NZB-level affinity takes priority (if not excluded or low success rate)
            .Where(x => x is not null)
            .Select(x => x!)
            .Distinct();
    }

    public Task ForceReleaseConnections(ConnectionUsageType? type = null)
    {
        var tasks = Providers.Select(p => p.ForceReleaseConnections(type));
        return Task.WhenAll(tasks);
    }

    public void Dispose()
    {
        foreach (var provider in Providers)
            provider.Dispose();
        GC.SuppressFinalize(this);
    }
}
