using System.Collections.Concurrent;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Registry for tracking STRM file download keys.
/// When an STRM file is generated, we register its downloadKey here.
/// When a request comes in with a downloadKey, we can identify it as STRM playback.
/// </summary>
public class StrmKeyRegistry
{
    private readonly ConcurrentDictionary<string, StrmKeyInfo> _keys = new();

    public static StrmKeyRegistry? Instance { get; private set; }

    public StrmKeyRegistry()
    {
        Instance = this;
    }

    /// <summary>
    /// Register a download key when an STRM file is generated
    /// </summary>
    public void Register(string downloadKey, Guid davItemId, string source, string filePath)
    {
        var info = new StrmKeyInfo
        {
            DavItemId = davItemId,
            Source = source,
            FilePath = filePath,
            CreatedAt = DateTime.UtcNow
        };
        _keys[downloadKey] = info;
        Log.Debug("[StrmKeyRegistry] Registered key for {Source}: {FilePath}", source, filePath);
    }

    /// <summary>
    /// Look up information about a download key
    /// </summary>
    public StrmKeyInfo? Lookup(string downloadKey)
    {
        return _keys.TryGetValue(downloadKey, out var info) ? info : null;
    }

    /// <summary>
    /// Check if a download key is for STRM playback
    /// </summary>
    public bool IsStrmPlayback(string? downloadKey)
    {
        return !string.IsNullOrEmpty(downloadKey) && _keys.ContainsKey(downloadKey);
    }

    /// <summary>
    /// Get count of registered keys (for diagnostics)
    /// </summary>
    public int Count => _keys.Count;

    /// <summary>
    /// Clean up old keys (older than specified age)
    /// </summary>
    public int CleanupOldKeys(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var keysToRemove = _keys
            .Where(kvp => kvp.Value.CreatedAt < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            _keys.TryRemove(key, out _);
        }

        if (keysToRemove.Count > 0)
        {
            Log.Information("[StrmKeyRegistry] Cleaned up {Count} old keys", keysToRemove.Count);
        }

        return keysToRemove.Count;
    }
}

/// <summary>
/// Information about a registered STRM download key
/// </summary>
public record StrmKeyInfo
{
    public Guid DavItemId { get; init; }
    public string Source { get; init; } = "";  // "Emby", "Jellyfin", etc.
    public string FilePath { get; init; } = "";
    public DateTime CreatedAt { get; init; }
}
