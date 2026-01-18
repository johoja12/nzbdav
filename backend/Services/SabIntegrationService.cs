using System.Text.Json;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Controls SABnzbd pause/resume based on streaming state.
/// Supports multiple SABnzbd servers and tracks which ones we paused.
/// </summary>
public class SabIntegrationService
{
    private readonly ConfigManager _configManager;
    private readonly HttpClient _httpClient;

    // Track which servers we paused (by URL)
    private readonly HashSet<string> _pausedByUs = new();
    private DateTimeOffset? _pausedAt = null;
    private readonly object _lock = new();

    public SabIntegrationService(ConfigManager configManager)
    {
        _configManager = configManager;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public bool IsPausedByUs
    {
        get { lock (_lock) return _pausedByUs.Count > 0; }
    }

    public DateTimeOffset? PausedAt
    {
        get { lock (_lock) return _pausedAt; }
    }

    /// <summary>
    /// Get effective list of SAB servers (handles legacy single-server config)
    /// </summary>
    private List<SabServer> GetEffectiveServers(SabPauseConfig config)
    {
        // If we have servers in the list, use those
        if (config.Servers.Count > 0)
        {
            return config.Servers.Where(s => s.Enabled).ToList();
        }

        // Fall back to legacy single-server config
        if (!string.IsNullOrEmpty(config.Url) && !string.IsNullOrEmpty(config.ApiKey))
        {
            return new List<SabServer>
            {
                new SabServer
                {
                    Name = "SABnzbd",
                    Url = config.Url,
                    ApiKey = config.ApiKey,
                    Enabled = true
                }
            };
        }

        return new List<SabServer>();
    }

    /// <summary>
    /// Pause all configured SABnzbd servers.
    /// </summary>
    public async Task<SabActionResult> PauseAsync(CancellationToken ct = default)
    {
        var config = _configManager.GetSabPauseConfig();

        if (!config.AutoPause)
        {
            return new SabActionResult { Success = true, Message = "Auto-pause disabled", Action = "none" };
        }

        var servers = GetEffectiveServers(config);
        if (servers.Count == 0)
        {
            return new SabActionResult { Success = true, Message = "No SABnzbd servers configured", Action = "none" };
        }

        int pausedCount = 0;
        int alreadyPausedCount = 0;
        int failedCount = 0;
        var errors = new List<string>();

        foreach (var server in servers)
        {
            try
            {
                var status = await GetServerStatusAsync(server, ct);

                if (status.IsPaused)
                {
                    Log.Debug("[SabIntegration] {Server} already paused, skipping", server.Name);
                    alreadyPausedCount++;
                    continue;
                }

                var pauseUrl = $"{server.Url.TrimEnd('/')}/api?mode=pause&apikey={server.ApiKey}";
                await _httpClient.GetStringAsync(pauseUrl, ct);

                lock (_lock)
                {
                    _pausedByUs.Add(server.Url);
                    _pausedAt ??= DateTimeOffset.UtcNow;
                }

                Log.Information("[SabIntegration] Paused {Server}", server.Name);
                pausedCount++;
            }
            catch (Exception ex)
            {
                Log.Warning("[SabIntegration] Failed to pause {Server}: {Error}", server.Name, ex.Message);
                errors.Add($"{server.Name}: {ex.Message}");
                failedCount++;
            }
        }

        var message = $"Paused: {pausedCount}, Already paused: {alreadyPausedCount}, Failed: {failedCount}";
        return new SabActionResult
        {
            Success = failedCount == 0,
            Message = message,
            Action = pausedCount > 0 ? "paused" : "none"
        };
    }

    /// <summary>
    /// Resume all SABnzbd servers that we paused.
    /// </summary>
    public async Task<SabActionResult> ResumeAsync(CancellationToken ct = default)
    {
        var config = _configManager.GetSabPauseConfig();

        if (!config.AutoPause)
        {
            return new SabActionResult { Success = true, Message = "Auto-pause disabled", Action = "none" };
        }

        var servers = GetEffectiveServers(config);
        if (servers.Count == 0)
        {
            return new SabActionResult { Success = true, Message = "No SABnzbd servers configured", Action = "none" };
        }

        int resumedCount = 0;
        int skippedCount = 0;
        int failedCount = 0;

        foreach (var server in servers)
        {
            bool shouldResume;
            lock (_lock)
            {
                shouldResume = _pausedByUs.Contains(server.Url);
            }

            if (!shouldResume)
            {
                Log.Debug("[SabIntegration] We didn't pause {Server}, skipping resume", server.Name);
                skippedCount++;
                continue;
            }

            try
            {
                var resumeUrl = $"{server.Url.TrimEnd('/')}/api?mode=resume&apikey={server.ApiKey}";
                await _httpClient.GetStringAsync(resumeUrl, ct);

                lock (_lock)
                {
                    _pausedByUs.Remove(server.Url);
                    if (_pausedByUs.Count == 0)
                    {
                        _pausedAt = null;
                    }
                }

                Log.Information("[SabIntegration] Resumed {Server}", server.Name);
                resumedCount++;
            }
            catch (Exception ex)
            {
                Log.Warning("[SabIntegration] Failed to resume {Server}: {Error}", server.Name, ex.Message);
                failedCount++;
            }
        }

        var message = $"Resumed: {resumedCount}, Skipped: {skippedCount}, Failed: {failedCount}";
        return new SabActionResult
        {
            Success = failedCount == 0,
            Message = message,
            Action = resumedCount > 0 ? "resumed" : "none"
        };
    }

    /// <summary>
    /// Get status of a specific server.
    /// </summary>
    private async Task<SabServerStatus> GetServerStatusAsync(SabServer server, CancellationToken ct)
    {
        var statusUrl = $"{server.Url.TrimEnd('/')}/api?mode=queue&output=json&apikey={server.ApiKey}";
        var response = await _httpClient.GetStringAsync(statusUrl, ct);

        using var doc = JsonDocument.Parse(response);
        var queue = doc.RootElement.GetProperty("queue");
        var paused = queue.GetProperty("paused").GetBoolean();
        var speedString = queue.TryGetProperty("speed", out var speedProp) ? speedProp.GetString() : "0";

        bool pausedByUs;
        lock (_lock)
        {
            pausedByUs = _pausedByUs.Contains(server.Url);
        }

        return new SabServerStatus
        {
            Name = server.Name,
            IsPaused = paused,
            Speed = speedString ?? "0",
            PausedByUs = pausedByUs
        };
    }

    /// <summary>
    /// Get combined status of all SABnzbd servers.
    /// </summary>
    public async Task<SabStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var config = _configManager.GetSabPauseConfig();
        var servers = GetEffectiveServers(config);

        if (servers.Count == 0)
        {
            return new SabStatus { IsConfigured = false };
        }

        var serverStatuses = new List<SabServerStatus>();

        foreach (var server in servers)
        {
            try
            {
                var status = await GetServerStatusAsync(server, ct);
                serverStatuses.Add(status);
            }
            catch (Exception ex)
            {
                serverStatuses.Add(new SabServerStatus
                {
                    Name = server.Name,
                    Error = ex.Message
                });
            }
        }

        return new SabStatus
        {
            IsConfigured = true,
            Servers = serverStatuses,
            IsPaused = serverStatuses.All(s => s.IsPaused || s.Error != null),
            PausedByUs = _pausedByUs.Count > 0
        };
    }

    /// <summary>
    /// Test connection to a SABnzbd server.
    /// </summary>
    public async Task<SabConnectionTestResult> TestConnection(string url, string apiKey, CancellationToken ct = default)
    {
        try
        {
            var versionUrl = $"{url.TrimEnd('/')}/api?mode=version&output=json&apikey={apiKey}";
            var versionResponse = await _httpClient.GetStringAsync(versionUrl, ct);
            using var versionDoc = JsonDocument.Parse(versionResponse);
            var version = versionDoc.RootElement.GetProperty("version").GetString();

            var statusUrl = $"{url.TrimEnd('/')}/api?mode=queue&output=json&apikey={apiKey}";
            var statusResponse = await _httpClient.GetStringAsync(statusUrl, ct);
            using var statusDoc = JsonDocument.Parse(statusResponse);
            var queue = statusDoc.RootElement.GetProperty("queue");
            var paused = queue.GetProperty("paused").GetBoolean();

            return new SabConnectionTestResult
            {
                Connected = true,
                Version = version,
                IsPaused = paused
            };
        }
        catch (HttpRequestException ex)
        {
            return new SabConnectionTestResult
            {
                Connected = false,
                Error = $"Connection failed: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            return new SabConnectionTestResult
            {
                Connected = false,
                Error = $"Error: {ex.Message}"
            };
        }
    }
}

public record SabActionResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string Action { get; init; } = "";
}

public record SabServerStatus
{
    public string Name { get; init; } = "";
    public bool IsPaused { get; init; }
    public string Speed { get; init; } = "";
    public bool PausedByUs { get; init; }
    public string? Error { get; init; }
}

public record SabStatus
{
    public bool IsConfigured { get; init; }
    public bool IsPaused { get; init; }
    public bool PausedByUs { get; init; }
    public List<SabServerStatus> Servers { get; init; } = new();
    public string? Error { get; init; }
}

public record SabConnectionTestResult
{
    public bool Connected { get; init; }
    public string? Version { get; init; }
    public bool? IsPaused { get; init; }
    public string? Error { get; init; }
}
