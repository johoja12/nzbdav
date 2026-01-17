using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Verifies if NZBDav streaming is for actual user playback by checking Emby sessions.
/// Unlike Plex, Emby does NOT have background activity that streams through NZBDav,
/// so all Emby streams through STRM files are real playback.
/// Implements IHostedService for proactive background polling with health tracking.
/// </summary>
public class EmbyVerificationService : IHostedService, IDisposable
{
    private readonly ConfigManager _configManager;
    private readonly HttpClient _httpClient;
    private readonly WebhookService _webhookService;

    // Cache for active session file names (refreshed periodically)
    private HashSet<string> _activeSessionFiles = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _cacheLastUpdated = DateTime.MinValue;
    private readonly object _cacheLock = new();
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromSeconds(3);
    private bool _cacheRefreshSucceeded = false;

    // Background polling for proactive health tracking
    private readonly ConcurrentDictionary<string, ServerHealth> _serverHealth = new();
    private Timer? _pollTimer;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>
    /// Static instance for access from non-DI contexts.
    /// Set automatically when the service is created via DI.
    /// </summary>
    public static EmbyVerificationService? Instance { get; private set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public EmbyVerificationService(ConfigManager configManager, WebhookService webhookService)
    {
        _configManager = configManager;
        _webhookService = webhookService;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        Instance = this;
    }

    /// <summary>
    /// Start background polling for proactive session cache and health tracking.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var config = _configManager.GetEmbyConfig();

        if (config.VerifyPlayback && config.Servers.Any(s => s.Enabled))
        {
            var pollInterval = _configManager.GetServerPollIntervalSeconds();
            _pollTimer = new Timer(
                PollServersCallback,
                null,
                TimeSpan.FromSeconds(5),  // Initial delay
                TimeSpan.FromSeconds(pollInterval));
            Log.Information("[EmbyVerify] Background polling started ({Interval}s interval)", pollInterval);
        }
        else
        {
            Log.Debug("[EmbyVerify] Background polling not started (verification disabled or no enabled servers)");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop background polling gracefully.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Log.Information("[EmbyVerify] Stopping background polling...");

        _cts?.Cancel();

        if (_pollTimer != null)
        {
            await _pollTimer.DisposeAsync();
            _pollTimer = null;
        }
    }

    private async void PollServersCallback(object? state)
    {
        if (_cts?.Token.IsCancellationRequested ?? true)
            return;

        try
        {
            await RefreshSessionCacheAsync();
            await CheckServerHealthAsync();
        }
        catch (Exception ex)
        {
            Log.Warning("[EmbyVerify] Background poll failed: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// Check health of all enabled Emby servers and fire webhooks on state changes.
    /// </summary>
    private async Task CheckServerHealthAsync()
    {
        var config = _configManager.GetEmbyConfig();
        var enabledServers = config.Servers.Where(s => s.Enabled).ToList();

        foreach (var server in enabledServers)
        {
            var previousHealth = _serverHealth.GetValueOrDefault(server.Name);
            bool wasReachable = previousHealth?.IsReachable ?? true;

            try
            {
                // Quick health check using existing GetServerSessionFilesAsync
                await GetServerSessionFilesAsync(server, _cts?.Token ?? CancellationToken.None);

                var newHealth = new ServerHealth
                {
                    ServerName = server.Name,
                    ServerType = "emby",
                    IsReachable = true,
                    LastChecked = DateTime.UtcNow,
                    LastReachable = DateTime.UtcNow,
                    ConsecutiveFailures = 0
                };
                _serverHealth[server.Name] = newHealth;

                // Fire recovery event if previously unreachable
                if (!wasReachable && previousHealth != null)
                {
                    await OnServerRecovered(server, previousHealth);
                }
            }
            catch (Exception ex)
            {
                var failures = (previousHealth?.ConsecutiveFailures ?? 0) + 1;
                var newHealth = new ServerHealth
                {
                    ServerName = server.Name,
                    ServerType = "emby",
                    IsReachable = false,
                    LastChecked = DateTime.UtcNow,
                    LastReachable = previousHealth?.LastReachable,
                    LastError = ex.Message,
                    ConsecutiveFailures = failures
                };
                _serverHealth[server.Name] = newHealth;

                // Fire unreachable event after 3 consecutive failures (avoid flapping)
                if (wasReachable && failures >= 3)
                {
                    await OnServerUnreachable(server, newHealth);
                }
            }
        }
    }

    private async Task OnServerUnreachable(EmbyServer server, ServerHealth health)
    {
        Log.Warning("[EmbyVerify] Server {Name} is unreachable: {Error}",
            server.Name, health.LastError);

        await _webhookService.FireEventAsync("server.unreachable", new
        {
            timestamp = DateTime.UtcNow,
            serverType = "emby",
            serverName = server.Name,
            serverUrl = server.Url,
            error = health.LastError,
            consecutiveFailures = health.ConsecutiveFailures
        });
    }

    private async Task OnServerRecovered(EmbyServer server, ServerHealth previousHealth)
    {
        var downtime = previousHealth.LastChecked != default
            ? DateTime.UtcNow - previousHealth.LastChecked
            : TimeSpan.Zero;

        Log.Information("[EmbyVerify] Server {Name} recovered after {Downtime}",
            server.Name, downtime);

        await _webhookService.FireEventAsync("server.recovered", new
        {
            timestamp = DateTime.UtcNow,
            serverType = "emby",
            serverName = server.Name,
            serverUrl = server.Url,
            downtimeSeconds = downtime.TotalSeconds
        });
    }

    /// <summary>
    /// Get current health status of all configured Emby servers.
    /// </summary>
    public IReadOnlyDictionary<string, ServerHealth> GetServerHealthStatus()
    {
        return _serverHealth.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        _cts?.Dispose();
        _pollTimer?.Dispose();
        _httpClient.Dispose();

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Check if a specific file is currently being played in an active Emby session.
    /// Uses cached session data (refreshed every 3 seconds) for fast lookups.
    /// </summary>
    public bool IsFilePlaying(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        var config = _configManager.GetEmbyConfig();
        if (!config.VerifyPlayback || config.Servers.Count == 0)
        {
            // If verification disabled, assume all streaming is real playback
            return true;
        }

        RefreshCacheIfNeeded();

        var filename = Path.GetFileName(filePath);
        var filenameWithoutExt = Path.GetFileNameWithoutExtension(filePath);

        lock (_cacheLock)
        {
            if (_activeSessionFiles.Count == 0)
            {
                if (_cacheRefreshSucceeded)
                {
                    Log.Debug("[EmbyVerify] IsFilePlaying({File}): False (no active Emby sessions)",
                        filename);
                    return false;
                }
                else
                {
                    Log.Debug("[EmbyVerify] IsFilePlaying({File}): True (cache refresh failed, assuming real playback)",
                        filename);
                    return true;
                }
            }

            if (_activeSessionFiles.Contains(filename))
            {
                Log.Debug("[EmbyVerify] IsFilePlaying({File}): True (exact match, cache has {Count} files)",
                    filename, _activeSessionFiles.Count);
                return true;
            }

            foreach (var sessionFile in _activeSessionFiles)
            {
                var sessionWithoutExt = Path.GetFileNameWithoutExtension(sessionFile);
                if (sessionWithoutExt.StartsWith(filenameWithoutExt, StringComparison.OrdinalIgnoreCase) ||
                    filenameWithoutExt.StartsWith(sessionWithoutExt, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Debug("[EmbyVerify] IsFilePlaying({File}): True (fuzzy match with {SessionFile})",
                        filename, sessionFile);
                    return true;
                }
            }

            Log.Debug("[EmbyVerify] IsFilePlaying({File}): False (not in {Count} active sessions)",
                filename, _activeSessionFiles.Count);
            return false;
        }
    }

    /// <summary>
    /// Simple check if any configured Emby server has active playback.
    /// </summary>
    public async Task<bool> IsAnyServerPlaying(CancellationToken ct = default)
    {
        var config = _configManager.GetEmbyConfig();
        if (!config.VerifyPlayback) return false;

        RefreshCacheIfNeeded();
        lock (_cacheLock) { return _activeSessionFiles.Count > 0; }
    }

    /// <summary>
    /// Test connection to an Emby server using URL and API key.
    /// </summary>
    public async Task<EmbyConnectionTestResult> TestConnection(string url, string apiKey, CancellationToken ct = default)
    {
        try
        {
            var sessionsUrl = $"{url.TrimEnd('/')}/Sessions?api_key={apiKey}";
            var response = await _httpClient.GetStringAsync(sessionsUrl, ct).ConfigureAwait(false);
            var sessions = JsonSerializer.Deserialize<List<EmbySession>>(response, JsonOptions);

            // Get server info for name
            string? serverName = null;
            try
            {
                var infoUrl = $"{url.TrimEnd('/')}/System/Info?api_key={apiKey}";
                var infoResponse = await _httpClient.GetStringAsync(infoUrl, ct).ConfigureAwait(false);
                var info = JsonSerializer.Deserialize<EmbyServerInfo>(infoResponse, JsonOptions);
                serverName = info?.ServerName;
            }
            catch
            {
                // Ignore - server name is optional
            }

            return new EmbyConnectionTestResult
            {
                Connected = true,
                ServerName = serverName ?? "Emby Server",
                ActiveSessions = sessions?.Count(s => s.NowPlayingItem != null) ?? 0
            };
        }
        catch (HttpRequestException ex)
        {
            return new EmbyConnectionTestResult
            {
                Connected = false,
                Error = $"Connection failed: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            return new EmbyConnectionTestResult
            {
                Connected = false,
                Error = $"Error: {ex.Message}"
            };
        }
    }

    private void RefreshCacheIfNeeded()
    {
        lock (_cacheLock)
        {
            if (DateTime.UtcNow - _cacheLastUpdated < _cacheExpiry)
                return;
        }

        try
        {
            var task = RefreshSessionCacheAsync();
            task.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            Log.Warning("[EmbyVerify] Failed to refresh session cache: {Error}", ex.Message);
            lock (_cacheLock) { _cacheRefreshSucceeded = false; }
        }
    }

    private async Task RefreshSessionCacheAsync()
    {
        var config = _configManager.GetEmbyConfig();
        var enabledServers = config.Servers.Where(s => s.Enabled).ToList();

        if (enabledServers.Count == 0)
        {
            lock (_cacheLock)
            {
                _activeSessionFiles.Clear();
                _cacheLastUpdated = DateTime.UtcNow;
                _cacheRefreshSucceeded = true;
            }
            return;
        }

        var allFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var server in enabledServers)
        {
            try
            {
                var files = await GetServerSessionFilesAsync(server).ConfigureAwait(false);
                foreach (var file in files)
                {
                    allFiles.Add(file);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("[EmbyVerify] Failed to get sessions from {Server}: {Error}", server.Name, ex.Message);
            }
        }

        lock (_cacheLock)
        {
            _activeSessionFiles = allFiles;
            _cacheLastUpdated = DateTime.UtcNow;
            _cacheRefreshSucceeded = true;
        }

        if (allFiles.Count > 0)
        {
            Log.Debug("[EmbyVerify] Cache refreshed with {Count} active files: {Files}",
                allFiles.Count, string.Join(", ", allFiles.Take(5)));
        }
    }

    private async Task<List<string>> GetServerSessionFilesAsync(EmbyServer server, CancellationToken ct = default)
    {
        var url = $"{server.Url.TrimEnd('/')}/Sessions?api_key={server.ApiKey}";
        var response = await _httpClient.GetStringAsync(url, ct).ConfigureAwait(false);
        var sessions = JsonSerializer.Deserialize<List<EmbySession>>(response, JsonOptions);

        var files = new List<string>();

        if (sessions == null) return files;

        foreach (var session in sessions)
        {
            // Only include sessions that are actively playing (NowPlayingItem present)
            // IsPaused == true still counts as "real" for SAB pausing purposes
            if (session.NowPlayingItem?.Path != null)
            {
                var filename = Path.GetFileName(session.NowPlayingItem.Path);
                if (!string.IsNullOrEmpty(filename))
                {
                    files.Add(filename);
                    Log.Debug("[EmbyVerify] Active session file: {File} (Paused: {Paused})",
                        filename, session.PlayState?.IsPaused ?? false);
                }
            }
        }

        return files;
    }
}

// JSON models for Emby API
public class EmbySession
{
    public string? Id { get; set; }
    public string? UserName { get; set; }
    public EmbyNowPlayingItem? NowPlayingItem { get; set; }
    public EmbyPlayState? PlayState { get; set; }
}

public class EmbyNowPlayingItem
{
    public string? Name { get; set; }
    public string? Path { get; set; }
    public string? Type { get; set; }
}

public class EmbyPlayState
{
    public bool IsPaused { get; set; }
    public long? PositionTicks { get; set; }
}

public class EmbyServerInfo
{
    public string? ServerName { get; set; }
    public string? Version { get; set; }
}

public class EmbyConnectionTestResult
{
    public bool Connected { get; set; }
    public string? ServerName { get; set; }
    public int ActiveSessions { get; set; }
    public string? Error { get; set; }
}
