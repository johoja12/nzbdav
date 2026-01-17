using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Websocket;
using System.Text.Json;

namespace NzbWebDAV.Clients.Usenet.Connections;

public class ConnectionPoolStats
{
    private readonly int[] _live;
    private readonly int[] _idle;
    private readonly int _max;
    private int _totalLive;
    private int _totalIdle;
    private int _lastStreamCount;
    private readonly UsenetProviderConfig _providerConfig;
    private readonly WebsocketManager _websocketManager;
    private readonly ConnectionPool<INntpClient>[] _connectionPools;

    /// <summary>
    /// Event fired when streaming connection count changes.
    /// Used by StreamingMonitorService for SABnzbd auto-pause.
    /// </summary>
    public event EventHandler<StreamingChangedEventArgs>? OnStreamingChanged;

    public ConnectionPoolStats(UsenetProviderConfig providerConfig, WebsocketManager websocketManager)
    {
        var count = providerConfig.Providers.Count;
        _live = new int[count];
        _idle = new int[count];
        _connectionPools = new ConnectionPool<INntpClient>[count];
        _max = providerConfig.Providers
            .Where(x => x.Type == ProviderType.Pooled)
            .Select(x => x.MaxConnections)
            .Sum();

        _providerConfig = providerConfig;
        _websocketManager = websocketManager;
    }

    public void RegisterConnectionPool(int providerIndex, ConnectionPool<INntpClient> connectionPool)
    {
        _connectionPools[providerIndex] = connectionPool;
    }

    public List<ConnectionUsageContext> GetActiveConnections()
    {
        var list = new List<ConnectionUsageContext>();
        foreach (var pool in _connectionPools)
        {
            if (pool != null)
                list.AddRange(pool.GetActiveConnections());
        }
        return list;
    }

    public Dictionary<int, List<ConnectionUsageContext>> GetActiveConnectionsByProvider()
    {
        var result = new Dictionary<int, List<ConnectionUsageContext>>();
        for (int i = 0; i < _connectionPools.Length; i++)
        {
            var pool = _connectionPools[i];
            if (pool != null)
                result[i] = pool.GetActiveConnections();
        }
        return result;
    }

    /// <summary>
    /// Gets the current count of streaming connections (all streaming types).
    /// </summary>
    public int GetStreamingConnectionCount()
    {
        var allConns = GetActiveConnections();
        return allConns.Count(c =>
            c.UsageType == ConnectionUsageType.Streaming ||
            c.UsageType == ConnectionUsageType.BufferedStreaming ||
            c.UsageType == ConnectionUsageType.PlexPlayback ||
            c.UsageType == ConnectionUsageType.PlexBackground ||
            c.UsageType == ConnectionUsageType.EmbyStrmPlayback);
    }

    public EventHandler<ConnectionPoolChangedEventArgs> GetOnConnectionPoolChanged(int providerIndex)
    {
        return OnEvent;

        void OnEvent(object? _, ConnectionPoolChangedEventArgs args)
        {
            if (_providerConfig.Providers[providerIndex].Type == ProviderType.Pooled)
            {
                lock (this)
                {
                    _live[providerIndex] = args.Live;
                    _idle[providerIndex] = args.Idle;
                    _totalLive = _live.Sum();
                    _totalIdle = _idle.Sum();
                }
            }

            // Get usage breakdown from all connection pools
            var usageBreakdown = GetGlobalUsageBreakdown();
            var providerBreakdown = GetProviderUsageBreakdown(providerIndex);
            
            // Get detailed connections for this provider to send over websocket
            var activeConns = _connectionPools[providerIndex]?.GetActiveConnections() ?? new List<ConnectionUsageContext>();
            var connsJson = JsonSerializer.Serialize(activeConns.Select(c => new {
                t = (int)c.UsageType,
                d = c.Details,
                jn = c.JobName,
                b = c.IsBackup,
                s = c.IsSecondary,
                bc = c.DetailsObject?.BufferedCount,
                ws = c.DetailsObject?.BufferWindowStart,
                we = c.DetailsObject?.BufferWindowEnd,
                ts = c.DetailsObject?.TotalSegments,
                i = c.DetailsObject?.DavItemId,
                bp = c.DetailsObject?.CurrentBytePosition,
                fs = c.DetailsObject?.FileSize
            }));

            var message = $"{providerIndex}|{args.Live}|{args.Idle}|{_totalLive}|{_max}|{_totalIdle}|{usageBreakdown}|{providerBreakdown}|{connsJson}";
            _websocketManager.SendMessage(WebsocketTopic.UsenetConnections, message);

            // Check for streaming count changes and fire event
            NotifyStreamingChangedIfNeeded();
        }
    }

    private void NotifyStreamingChangedIfNeeded()
    {
        var currentStreamCount = GetStreamingConnectionCount();

        int previousCount;
        lock (this)
        {
            if (currentStreamCount == _lastStreamCount)
                return;

            previousCount = _lastStreamCount;
            _lastStreamCount = currentStreamCount;
        }

        // Only fire event if there was an actual change
        OnStreamingChanged?.Invoke(this, new StreamingChangedEventArgs
        {
            ActiveStreamCount = currentStreamCount,
            PreviousStreamCount = previousCount
        });
    }

    private string GetGlobalUsageBreakdown()
    {
        var allUsageCounts = new Dictionary<ConnectionUsageType, int>();

        foreach (var pool in _connectionPools)
        {
            if (pool == null) continue;

            var breakdown = pool.GetUsageBreakdown();
            foreach (var (usageType, count) in breakdown)
            {
                allUsageCounts.TryGetValue(usageType, out var currentCount);
                allUsageCounts[usageType] = currentCount + count;
            }
        }

        var parts = allUsageCounts
            .OrderBy(x => x.Key)
            .Select(kv => $"{kv.Key}={kv.Value}")
            .ToArray();

        return parts.Length > 0 ? string.Join(",", parts) : "none";
    }

    private string GetProviderUsageBreakdown(int providerIndex)
    {
        var pool = _connectionPools[providerIndex];
        if (pool == null) return "none";

        var breakdown = pool.GetUsageBreakdown();
        var parts = breakdown
            .OrderBy(x => x.Key)
            .Select(kv => $"{kv.Key}={kv.Value}")
            .ToArray();

        return parts.Length > 0 ? string.Join(",", parts) : "none";
    }

    public sealed class ConnectionPoolChangedEventArgs(int live, int idle, int max) : EventArgs
    {
        public int Live { get; } = live;
        public int Idle { get; } = idle;
        public int Max { get; } = max;
        public int Active => Live - Idle;
    }
}

/// <summary>
/// Event args for streaming state changes
/// </summary>
public class StreamingChangedEventArgs : EventArgs
{
    public int ActiveStreamCount { get; init; }
    public int PreviousStreamCount { get; init; }
}