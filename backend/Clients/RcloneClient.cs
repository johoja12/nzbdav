using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Clients;

/// <summary>
/// Client for rclone Remote Control (RC) API.
/// Used to trigger cache-warming reads through rclone VFS.
/// </summary>
public class RcloneClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly RcloneInstance _instance;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public RcloneClient(RcloneInstance instance)
    {
        _instance = instance;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(instance.GetBaseUrl()),
            Timeout = TimeSpan.FromSeconds(30)
        };

        if (!string.IsNullOrEmpty(instance.Username) && !string.IsNullOrEmpty(instance.Password))
        {
            var authBytes = Encoding.ASCII.GetBytes($"{instance.Username}:{instance.Password}");
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
        }
    }

    /// <summary>
    /// Check if this rclone instance handles a specific file ID based on shard configuration.
    /// Returns true if sharding is disabled or if the file's ID matches this instance's shard.
    /// </summary>
    public bool HandlesFileId(Guid fileId)
    {
        return _instance.HandlesFileId(fileId);
    }

    /// <summary>
    /// Whether shard routing is enabled for this instance.
    /// </summary>
    public bool IsShardEnabled => _instance.IsShardEnabled;

    /// <summary>
    /// Get the .ids path for a file within this instance's mount.
    /// When sharding is enabled, returns the path relative to the instance root.
    /// </summary>
    public string GetIdsPathForFile(Guid fileId)
    {
        var idsPath = ShardRoutingUtil.GetIdsPathForId(fileId);
        return $".ids/{idsPath}";
    }

    /// <summary>
    /// Get the instance's shard prefixes (e.g., "0-3" or "0,1,2,3").
    /// Returns null if sharding is not enabled.
    /// </summary>
    public string? ShardPrefixes => _instance.IsShardEnabled ? _instance.ShardPrefixes : null;

    public async Task<RcloneTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var stats = await GetCoreStatsAsync(ct).ConfigureAwait(false);
            return new RcloneTestResult
            {
                Success = true,
                Version = stats?.Version,
                Message = $"Connected to rclone {stats?.Version ?? "unknown"}"
            };
        }
        catch (HttpRequestException ex)
        {
            return new RcloneTestResult { Success = false, Message = $"Connection failed: {ex.Message}" };
        }
        catch (TaskCanceledException)
        {
            return new RcloneTestResult { Success = false, Message = "Connection timed out" };
        }
        catch (Exception ex)
        {
            return new RcloneTestResult { Success = false, Message = $"Error: {ex.Message}" };
        }
    }

    public async Task<RcloneVersionInfo?> GetVersionAsync(CancellationToken ct = default)
    {
        return await PostAsync<object, RcloneVersionInfo>("core/version", new { }, ct).ConfigureAwait(false);
    }

    public async Task<RcloneCoreStats?> GetCoreStatsAsync(CancellationToken ct = default)
    {
        return await PostAsync<object, RcloneCoreStats>("core/stats", new { }, ct).ConfigureAwait(false);
    }

    public async Task<RcloneVfsStats?> GetVfsStatsAsync(CancellationToken ct = default)
    {
        return await PostAsync<object, RcloneVfsStats>("vfs/stats", new { fs = _instance.RemoteName }, ct).ConfigureAwait(false);
    }

    public async Task<RcloneCoreStatsExtended?> GetCoreStatsExtendedAsync(CancellationToken ct = default)
    {
        return await PostAsync<object, RcloneCoreStatsExtended>("core/stats", new { }, ct).ConfigureAwait(false);
    }

    public async Task<RcloneVfsStatsExtended?> GetVfsStatsExtendedAsync(CancellationToken ct = default)
    {
        return await PostAsync<object, RcloneVfsStatsExtended>("vfs/stats", new { fs = _instance.RemoteName }, ct).ConfigureAwait(false);
    }

    public async Task<RcloneVfsTransfersResponse?> GetVfsTransfersAsync(CancellationToken ct = default)
    {
        try
        {
            return await PostAsync<object, RcloneVfsTransfersResponse>("vfs/transfers", new { }, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<RcloneReadResult> VfsReadAsync(string path, long offset, long count, CancellationToken ct = default)
    {
        try
        {
            var request = new { fs = _instance.RemoteName, path = path.TrimStart('/'), offset, count };
            var response = await PostRawAsync("vfs/read", request, ct).ConfigureAwait(false);
            return new RcloneReadResult { Success = true, BytesRead = response?.Length ?? 0 };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RcloneClient] VFS read failed for {Path} at offset {Offset}", path, offset);
            return new RcloneReadResult { Success = false, Error = ex.Message };
        }
    }

    public async Task<bool> VfsRefreshAsync(string path = "", bool recursive = false, CancellationToken ct = default)
    {
        try
        {
            await PostAsync<object, object>("vfs/refresh", new { fs = _instance.RemoteName, dir = path, recursive }, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RcloneClient] VFS refresh failed for {Path}", path);
            return false;
        }
    }

    public async Task<List<string>> ListRemotesAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await PostAsync<object, RcloneRemotesResult>("config/listremotes", new { }, ct).ConfigureAwait(false);
            return result?.Remotes ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private async Task<TResponse?> PostAsync<TRequest, TResponse>(string endpoint, TRequest request, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(request, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(endpoint, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var responseJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TResponse>(responseJson, JsonOptions);
    }

    private async Task<byte[]?> PostRawAsync<TRequest>(string endpoint, TRequest request, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(request, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(endpoint, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public void Dispose() => _httpClient.Dispose();
}

public class RcloneTestResult
{
    public bool Success { get; init; }
    public string? Version { get; init; }
    public string Message { get; init; } = string.Empty;
}

public class RcloneReadResult
{
    public bool Success { get; init; }
    public long BytesRead { get; init; }
    public string? Error { get; init; }
}

public class RcloneCoreStats
{
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    [JsonPropertyName("transfers")] public int Transfers { get; set; }
    [JsonPropertyName("speed")] public double Speed { get; set; }
}

public class RcloneVfsStats
{
    [JsonPropertyName("diskCache")] public RcloneDiskCache? DiskCache { get; set; }
}

public class RcloneDiskCache
{
    [JsonPropertyName("bytesUsed")] public long BytesUsed { get; set; }
    [JsonPropertyName("uploadsInProgress")] public int UploadsInProgress { get; set; }
    [JsonPropertyName("uploadsQueued")] public int UploadsQueued { get; set; }
}

public class RcloneRemotesResult
{
    [JsonPropertyName("remotes")] public List<string> Remotes { get; set; } = new();
}

public class RcloneCoreStatsExtended
{
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    [JsonPropertyName("transfers")] public int Transfers { get; set; }
    [JsonPropertyName("speed")] public double Speed { get; set; }
    [JsonPropertyName("errors")] public int Errors { get; set; }
    [JsonPropertyName("lastError")] public string? LastError { get; set; }
    [JsonPropertyName("transferring")] public List<RcloneTransferItem>? Transferring { get; set; }
}

public class RcloneTransferItem
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("percentage")] public int Percentage { get; set; }
    [JsonPropertyName("speed")] public double Speed { get; set; }
    [JsonPropertyName("speedAvg")] public double SpeedAvg { get; set; }
    [JsonPropertyName("eta")] public int? Eta { get; set; }
}

public class RcloneVfsStatsExtended
{
    [JsonPropertyName("diskCache")] public RcloneDiskCacheExtended? DiskCache { get; set; }
    [JsonPropertyName("opt")] public RcloneVfsOptions? Opt { get; set; }
}

public class RcloneDiskCacheExtended
{
    [JsonPropertyName("bytesUsed")] public long BytesUsed { get; set; }
    [JsonPropertyName("files")] public int Files { get; set; }
    [JsonPropertyName("outOfSpace")] public bool OutOfSpace { get; set; }
    [JsonPropertyName("uploadsInProgress")] public int UploadsInProgress { get; set; }
    [JsonPropertyName("uploadsQueued")] public int UploadsQueued { get; set; }
}

public class RcloneVfsOptions
{
    [JsonPropertyName("CacheMaxSize")] public long CacheMaxSize { get; set; }
}

public class RcloneVfsTransfersResponse
{
    [JsonPropertyName("summary")] public RcloneVfsTransfersSummary? Summary { get; set; }
    [JsonPropertyName("transfers")] public List<RcloneVfsTransferItem>? Transfers { get; set; }
}

public class RcloneVfsTransfersSummary
{
    [JsonPropertyName("activeDownloads")] public int ActiveDownloads { get; set; }
    [JsonPropertyName("activeReads")] public int ActiveReads { get; set; }
    [JsonPropertyName("totalOpenFiles")] public int TotalOpenFiles { get; set; }
    [JsonPropertyName("outOfSpace")] public bool OutOfSpace { get; set; }
    [JsonPropertyName("totalCacheBytes")] public long TotalCacheBytes { get; set; }
    [JsonPropertyName("totalCacheFiles")] public int TotalCacheFiles { get; set; }
}

public class RcloneVfsTransferItem
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("opens")] public int Opens { get; set; }
    [JsonPropertyName("dirty")] public bool Dirty { get; set; }
    [JsonPropertyName("lastAccess")] public string? LastAccess { get; set; }
    [JsonPropertyName("cacheBytes")] public long CacheBytes { get; set; }
    [JsonPropertyName("cachePercentage")] public int CachePercentage { get; set; }
    [JsonPropertyName("cacheStatus")] public string CacheStatus { get; set; } = "none";
    [JsonPropertyName("downloading")] public bool Downloading { get; set; }
    [JsonPropertyName("downloadBytes")] public long DownloadBytes { get; set; }
    [JsonPropertyName("downloadSpeed")] public double DownloadSpeed { get; set; }
    [JsonPropertyName("downloadSpeedAvg")] public double DownloadSpeedAvg { get; set; }
    [JsonPropertyName("readBytes")] public long ReadBytes { get; set; }
    [JsonPropertyName("readOffset")] public long ReadOffset { get; set; }
    [JsonPropertyName("readOffsetPercentage")] public int ReadOffsetPercentage { get; set; }
    [JsonPropertyName("readSpeed")] public double ReadSpeed { get; set; }
}

public class RcloneVersionInfo
{
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("os")] public string? Os { get; set; }
    [JsonPropertyName("arch")] public string? Arch { get; set; }
    [JsonPropertyName("goVersion")] public string? GoVersion { get; set; }
}
