using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Per-server health status for Plex/Emby servers.
/// </summary>
public record ServerHealth
{
    public string ServerName { get; init; } = "";
    public string ServerType { get; init; } = "";  // "plex" or "emby"
    public bool IsReachable { get; init; } = true;
    public DateTime LastChecked { get; init; }
    public DateTime? LastReachable { get; init; }
    public string? LastError { get; init; }
    public int ConsecutiveFailures { get; init; }
}

/// <summary>
/// Verifies if NZBDav streaming is for actual user playback by checking Plex sessions.
/// Plex /status/sessions only shows real user playback - not intro detection, thumbnails, etc.
/// Implements IHostedService for proactive background polling with health tracking.
/// </summary>
public class PlexVerificationService : IHostedService, IDisposable
{
    private readonly ConfigManager _configManager;
    private readonly HttpClient _httpClient;
    private readonly WebhookService _webhookService;

    // Cache for active session file names (refreshed periodically)
    private HashSet<string> _activeSessionFiles = new(StringComparer.OrdinalIgnoreCase);
    // Cache for files being accessed by background activities (intro detection, thumbnails, etc.)
    private HashSet<string> _activeActivityFiles = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _cacheLastUpdated = DateTime.MinValue;
    private readonly object _cacheLock = new();
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromSeconds(3);

    // Background polling for proactive health tracking
    private readonly ConcurrentDictionary<string, ServerHealth> _serverHealth = new();
    private Timer? _pollTimer;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>
    /// Static instance for access from non-DI contexts (e.g., NzbFileStream).
    /// Set automatically when the service is created via DI.
    /// </summary>
    public static PlexVerificationService? Instance { get; private set; }

    public PlexVerificationService(ConfigManager configManager, WebhookService webhookService)
    {
        _configManager = configManager;
        _webhookService = webhookService;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        Instance = this; // Set static instance for non-DI access
    }

    /// <summary>
    /// Start background polling for proactive session cache and health tracking.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var config = _configManager.GetPlexConfig();

        if (config.VerifyPlayback && config.Servers.Any(s => s.Enabled))
        {
            var pollInterval = _configManager.GetServerPollIntervalSeconds();
            _pollTimer = new Timer(
                PollServersCallback,
                null,
                TimeSpan.FromSeconds(5),  // Initial delay
                TimeSpan.FromSeconds(pollInterval));
            Log.Information("[PlexVerify] Background polling started ({Interval}s interval)", pollInterval);
        }
        else
        {
            Log.Debug("[PlexVerify] Background polling not started (verification disabled or no enabled servers)");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop background polling gracefully.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Log.Information("[PlexVerify] Stopping background polling...");

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
            Log.Warning("[PlexVerify] Background poll failed: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// Check health of all enabled Plex servers and fire webhooks on state changes.
    /// </summary>
    private async Task CheckServerHealthAsync()
    {
        var config = _configManager.GetPlexConfig();
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
                    ServerType = "plex",
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
                    ServerType = "plex",
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

    private async Task OnServerUnreachable(PlexServer server, ServerHealth health)
    {
        Log.Warning("[PlexVerify] Server {Name} is unreachable: {Error}",
            server.Name, health.LastError);

        await _webhookService.FireEventAsync("server.unreachable", new
        {
            timestamp = DateTime.UtcNow,
            serverType = "plex",
            serverName = server.Name,
            serverUrl = server.Url,
            error = health.LastError,
            consecutiveFailures = health.ConsecutiveFailures
        });
    }

    private async Task OnServerRecovered(PlexServer server, ServerHealth previousHealth)
    {
        var downtime = previousHealth.LastChecked != default
            ? DateTime.UtcNow - previousHealth.LastChecked
            : TimeSpan.Zero;

        Log.Information("[PlexVerify] Server {Name} recovered after {Downtime}",
            server.Name, downtime);

        await _webhookService.FireEventAsync("server.recovered", new
        {
            timestamp = DateTime.UtcNow,
            serverType = "plex",
            serverName = server.Name,
            serverUrl = server.Url,
            downtimeSeconds = downtime.TotalSeconds
        });
    }

    /// <summary>
    /// Get current health status of all configured Plex servers.
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
    /// Check if a specific file is currently being played in an active Plex session.
    /// Uses cached session data (refreshed every 3 seconds) for fast lookups.
    /// </summary>
    /// <param name="filePath">Full path or filename to check</param>
    /// <returns>True if playing, False if not playing, null if verification disabled or not configured</returns>
    public bool? IsFilePlaying(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        var config = _configManager.GetPlexConfig();
        if (!config.VerifyPlayback || config.Servers.Count == 0)
        {
            // Verification disabled or no servers - return null to indicate "not configured"
            // This allows the caller to fall through to other classification logic
            return null;
        }

        // Refresh cache if stale
        RefreshCacheIfNeeded();

        // Extract filename from path for matching
        var filename = Path.GetFileName(filePath);
        var filenameWithoutExt = Path.GetFileNameWithoutExtension(filePath);

        // First check with current cache
        var (found, hadSessions) = CheckFileInCache(filename, filenameWithoutExt);
        if (found)
            return true;

        // If cache had sessions but file wasn't found, force an immediate refresh to double-check.
        if (hadSessions)
        {
            Log.Debug("[PlexVerify] IsFilePlaying({File}): Cache miss with {Count} active sessions, forcing refresh",
                filename, _activeSessionFiles.Count);
            ForceRefreshCache();

            // Check again after fresh refresh
            var (foundAfterRefresh, _) = CheckFileInCache(filename, filenameWithoutExt);
            if (foundAfterRefresh)
                return true;

            // File still not found after fresh refresh - there ARE active sessions but for OTHER files.
            // This means this file is NOT being played back by a user - it's background activity
            // (health checks, intro detection, thumbnail generation, etc.)
            Log.Debug("[PlexVerify] IsFilePlaying({File}): False (not in any active session, {Count} other sessions playing)",
                filename, _activeSessionFiles.Count);
            return false;
        }

        // No sessions in cache - this is NOT playback, treat as background/buffered streaming
        Log.Debug("[PlexVerify] IsFilePlaying({File}): False (no active Plex sessions)", filename);
        return false;
    }

    /// <summary>
    /// Check if file is in the current cache. Returns (found, hadSessions).
    /// </summary>
    private (bool found, bool hadSessions) CheckFileInCache(string filename, string filenameWithoutExt)
    {
        lock (_cacheLock)
        {
            if (_activeSessionFiles.Count == 0)
                return (false, false);

            // Try exact match first
            if (_activeSessionFiles.Contains(filename))
            {
                Log.Debug("[PlexVerify] IsFilePlaying({File}): True (exact match, cache has {Count} files)",
                    filename, _activeSessionFiles.Count);
                return (true, true);
            }

            // Try matching without extension (folder names vs filenames)
            foreach (var sessionFile in _activeSessionFiles)
            {
                var sessionWithoutExt = Path.GetFileNameWithoutExtension(sessionFile);

                // Check if either starts with the other (handles folder name vs filename matching)
                if (sessionWithoutExt.StartsWith(filenameWithoutExt, StringComparison.OrdinalIgnoreCase) ||
                    filenameWithoutExt.StartsWith(sessionWithoutExt, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Debug("[PlexVerify] IsFilePlaying({File}): True (prefix match with {SessionFile})",
                        filename, sessionFile);
                    return (true, true);
                }

                // Smart matching: compare normalized show name + episode identifier
                if (IsSemanticMatch(filenameWithoutExt, sessionWithoutExt))
                {
                    Log.Debug("[PlexVerify] IsFilePlaying({File}): True (semantic match with {SessionFile})",
                        filename, sessionFile);
                    return (true, true);
                }
            }

            return (false, true);
        }
    }

    /// <summary>
    /// Force an immediate cache refresh, bypassing the expiry check.
    /// </summary>
    private void ForceRefreshCache()
    {
        try
        {
            var task = RefreshSessionCacheAsync();
            task.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            Log.Warning("[PlexVerify] Failed to force refresh session cache: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// Get list of currently playing file names (for debugging/display)
    /// </summary>
    public IReadOnlyCollection<string> GetActiveSessionFiles()
    {
        RefreshCacheIfNeeded();
        lock (_cacheLock)
        {
            return _activeSessionFiles.ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Check if Plex has any active sessions at all.
    /// Used to distinguish between "no Plex activity" vs "Plex activity but not for this file".
    /// </summary>
    public bool HasAnyActiveSessions()
    {
        var config = _configManager.GetPlexConfig();
        if (!config.VerifyPlayback || config.Servers.Count == 0)
            return false;

        RefreshCacheIfNeeded();

        lock (_cacheLock)
        {
            return _activeSessionFiles.Count > 0;
        }
    }

    /// <summary>
    /// Check if a file is being accessed by a Plex background activity (not real playback).
    /// This includes intro detection, thumbnail generation, media analysis, etc.
    /// </summary>
    public bool IsFileInBackgroundActivity(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        var config = _configManager.GetPlexConfig();
        if (!config.VerifyPlayback || config.Servers.Count == 0)
            return false;

        RefreshCacheIfNeeded();

        var filename = Path.GetFileName(filePath);
        var filenameWithoutExt = Path.GetFileNameWithoutExtension(filePath);

        lock (_cacheLock)
        {
            if (_activeActivityFiles.Count == 0)
                return false;

            // Check for exact match
            if (_activeActivityFiles.Contains(filename))
            {
                Log.Debug("[PlexVerify] IsFileInBackgroundActivity({File}): True (exact match)", filename);
                return true;
            }

            // Check for prefix/semantic match
            foreach (var activityFile in _activeActivityFiles)
            {
                var activityWithoutExt = Path.GetFileNameWithoutExtension(activityFile);
                if (activityWithoutExt.StartsWith(filenameWithoutExt, StringComparison.OrdinalIgnoreCase) ||
                    filenameWithoutExt.StartsWith(activityWithoutExt, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Debug("[PlexVerify] IsFileInBackgroundActivity({File}): True (prefix match with {ActivityFile})", filename, activityFile);
                    return true;
                }
            }

            return false;
        }
    }

    private void RefreshCacheIfNeeded()
    {
        lock (_cacheLock)
        {
            if (DateTime.UtcNow - _cacheLastUpdated < _cacheExpiry)
                return;
        }

        // Refresh outside lock to avoid blocking
        try
        {
            var task = RefreshSessionCacheAsync();
            task.Wait(TimeSpan.FromSeconds(2)); // Don't block too long
        }
        catch (Exception ex)
        {
            Log.Warning("[PlexVerify] Failed to refresh session cache: {Error}", ex.Message);
        }
    }

    private async Task RefreshSessionCacheAsync()
    {
        var config = _configManager.GetPlexConfig();
        var enabledServers = config.Servers.Where(s => s.Enabled).ToList();

        if (enabledServers.Count == 0)
        {
            lock (_cacheLock)
            {
                _activeSessionFiles.Clear();
                _activeActivityFiles.Clear();
                _cacheLastUpdated = DateTime.UtcNow;
            }
            return;
        }

        var allSessionFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allActivityFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var server in enabledServers)
        {
            // Get active playback sessions
            try
            {
                var files = await GetServerSessionFilesAsync(server);
                foreach (var file in files)
                {
                    allSessionFiles.Add(file);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("[PlexVerify] Failed to get sessions from {Server}: {Error}", server.Name, ex.Message);
            }

            // Get background activities (intro detection, thumbnail generation, etc.)
            try
            {
                var activityFiles = await GetServerActivityFilesAsync(server);
                foreach (var file in activityFiles)
                {
                    allActivityFiles.Add(file);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("[PlexVerify] Failed to get activities from {Server}: {Error}", server.Name, ex.Message);
            }
        }

        lock (_cacheLock)
        {
            _activeSessionFiles = allSessionFiles;
            _activeActivityFiles = allActivityFiles;
            _cacheLastUpdated = DateTime.UtcNow;
        }

        if (allSessionFiles.Count > 0)
        {
            Log.Debug("[PlexVerify] Cache refreshed with {Count} active session files: {Files}",
                allSessionFiles.Count, string.Join(", ", allSessionFiles.Take(5)));
        }
        if (allActivityFiles.Count > 0)
        {
            Log.Debug("[PlexVerify] Found {Count} files in background activities: {Files}",
                allActivityFiles.Count, string.Join(", ", allActivityFiles.Take(5)));
        }
    }

    private async Task<List<string>> GetServerSessionFilesAsync(PlexServer server, CancellationToken ct = default)
    {
        var url = $"{server.Url.TrimEnd('/')}/status/sessions?X-Plex-Token={server.Token}";

        var response = await _httpClient.GetStringAsync(url, ct);
        var xml = XDocument.Parse(response);

        var files = new List<string>();

        // Get all Video elements with state="playing"
        var sessions = xml.Root?.Elements("Video")
            .Where(v => v.Element("Player")?.Attribute("state")?.Value == "playing")
            .ToList() ?? new List<XElement>();

        foreach (var session in sessions)
        {
            // First try to get file path directly from session (works for direct play)
            var filePath = session.Element("Media")?.Element("Part")?.Attribute("file")?.Value;

            // If not found (transcoded streams), query library metadata
            if (string.IsNullOrEmpty(filePath))
            {
                var key = session.Attribute("key")?.Value;
                if (!string.IsNullOrEmpty(key))
                {
                    try
                    {
                        filePath = await GetFilePathFromMetadata(server, key, ct);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("[PlexVerify] Failed to get metadata for {Key}: {Error}", key, ex.Message);
                    }
                }
            }

            if (!string.IsNullOrEmpty(filePath))
            {
                var filename = Path.GetFileName(filePath);
                if (!string.IsNullOrEmpty(filename))
                {
                    files.Add(filename);
                    Log.Debug("[PlexVerify] Active session file: {File}", filename);
                }
            }
        }

        return files;
    }

    private async Task<string?> GetFilePathFromMetadata(PlexServer server, string key, CancellationToken ct)
    {
        var metadataUrl = $"{server.Url.TrimEnd('/')}{key}?X-Plex-Token={server.Token}";
        var response = await _httpClient.GetStringAsync(metadataUrl, ct);
        var xml = XDocument.Parse(response);

        // Get file path from Media/Part/@file in library metadata
        return xml.Root?.Element("Video")?.Element("Media")?.Element("Part")?.Attribute("file")?.Value;
    }

    /// <summary>
    /// Get files being processed by background activities (intro detection, thumbnail generation, etc.)
    /// Uses the /activities endpoint which shows server background tasks.
    /// </summary>
    private async Task<List<string>> GetServerActivityFilesAsync(PlexServer server, CancellationToken ct = default)
    {
        var url = $"{server.Url.TrimEnd('/')}/activities?X-Plex-Token={server.Token}";

        var response = await _httpClient.GetStringAsync(url, ct);
        var xml = XDocument.Parse(response);

        var files = new List<string>();

        // Activities that involve media files have a Context element with a key pointing to the media item
        // Common activity types: library.analyze (intro detection), media.analyze, library.update.section.scan
        var activities = xml.Root?.Elements("Activity").ToList() ?? new List<XElement>();

        foreach (var activity in activities)
        {
            var type = activity.Attribute("type")?.Value ?? "";
            var subtitle = activity.Attribute("subtitle")?.Value ?? "";

            // These activity types involve reading media files
            if (type.Contains("analyze", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("scan", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("generate", StringComparison.OrdinalIgnoreCase))
            {
                // The subtitle often contains the media title
                // Context element has the library section key
                var context = activity.Element("Context");
                var key = context?.Attribute("key")?.Value;

                if (!string.IsNullOrEmpty(key))
                {
                    try
                    {
                        // Try to get the file path from the metadata
                        var filePath = await GetFilePathFromMetadata(server, key, ct);
                        if (!string.IsNullOrEmpty(filePath))
                        {
                            var filename = Path.GetFileName(filePath);
                            if (!string.IsNullOrEmpty(filename))
                            {
                                files.Add(filename);
                                Log.Debug("[PlexVerify] Background activity ({Type}): {File}", type, filename);
                            }
                        }
                    }
                    catch
                    {
                        // Ignore - key might not be a media item
                    }
                }

                // Also try to extract from subtitle if it looks like a filename
                if (!string.IsNullOrEmpty(subtitle) && (subtitle.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
                    subtitle.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                    subtitle.EndsWith(".avi", StringComparison.OrdinalIgnoreCase)))
                {
                    files.Add(Path.GetFileName(subtitle));
                }
            }
        }

        return files;
    }

    /// <summary>
    /// Simple check if any configured Plex server has active playback.
    /// Used by StreamingMonitorService for quick yes/no decisions.
    /// </summary>
    public async Task<bool> IsAnyServerPlaying(CancellationToken ct = default)
    {
        var result = await VerifyPlaybackAsync(ct);
        return result.IsRealPlayback && result.SessionCount > 0;
    }

    /// <summary>
    /// Check if any configured Plex server has active playback sessions.
    /// Returns true if ANY server has real playback happening.
    /// </summary>
    public async Task<PlexVerificationResult> VerifyPlaybackAsync(CancellationToken ct = default)
    {
        var config = _configManager.GetPlexConfig();

        if (!config.VerifyPlayback || config.Servers.Count == 0)
        {
            Log.Debug("[PlexVerify] Verification disabled or no servers configured, assuming real playback");
            return new PlexVerificationResult { IsRealPlayback = true, Reason = "Verification disabled" };
        }

        var enabledServers = config.Servers.Where(s => s.Enabled).ToList();
        if (enabledServers.Count == 0)
        {
            Log.Debug("[PlexVerify] No enabled Plex servers, assuming real playback");
            return new PlexVerificationResult { IsRealPlayback = true, Reason = "No enabled servers" };
        }

        int serversChecked = 0;
        int serversFailed = 0;

        foreach (var server in enabledServers)
        {
            try
            {
                var result = await CheckServerSessionsAsync(server, ct);
                serversChecked++;

                if (result.HasActiveSessions)
                {
                    Log.Information("[PlexVerify] Active playback on {Server}: {Count} session(s)",
                        server.Name, result.SessionCount);
                    return new PlexVerificationResult
                    {
                        IsRealPlayback = true,
                        Reason = $"Active session on {server.Name}",
                        SessionCount = result.SessionCount
                    };
                }
            }
            catch (Exception ex)
            {
                serversFailed++;
                Log.Warning("[PlexVerify] Failed to check {Server}: {Error}", server.Name, ex.Message);
            }
        }

        // If all servers failed, assume real playback (safe default)
        if (serversFailed == enabledServers.Count)
        {
            Log.Warning("[PlexVerify] All Plex servers unreachable, assuming real playback");
            return new PlexVerificationResult { IsRealPlayback = true, Reason = "All servers unreachable" };
        }

        // Checked servers successfully but no sessions found
        Log.Debug("[PlexVerify] No active Plex sessions found on {Count} server(s)", serversChecked);
        return new PlexVerificationResult
        {
            IsRealPlayback = false,
            Reason = "No active Plex sessions",
            SessionCount = 0
        };
    }

    private async Task<ServerSessionResult> CheckServerSessionsAsync(PlexServer server, CancellationToken ct)
    {
        var url = $"{server.Url.TrimEnd('/')}/status/sessions?X-Plex-Token={server.Token}";

        var response = await _httpClient.GetStringAsync(url, ct);
        var xml = XDocument.Parse(response);

        // Count Video elements with state="playing"
        var sessions = xml.Root?.Elements("Video")
            .Where(v => v.Element("Player")?.Attribute("state")?.Value == "playing")
            .ToList() ?? new List<XElement>();

        return new ServerSessionResult
        {
            HasActiveSessions = sessions.Count > 0,
            SessionCount = sessions.Count
        };
    }

    /// <summary>
    /// Test connection to a Plex server using URL and token.
    /// </summary>
    public async Task<PlexConnectionTestResult> TestConnection(string url, string token, CancellationToken ct = default)
    {
        try
        {
            // First try to get sessions
            var sessionsUrl = $"{url.TrimEnd('/')}/status/sessions?X-Plex-Token={token}";
            var response = await _httpClient.GetStringAsync(sessionsUrl, ct);
            var xml = XDocument.Parse(response);

            var sessionCount = xml.Root?.Elements("Video").Count() ?? 0;

            // Try to get server identity for name
            string? serverName = null;
            try
            {
                var identityUrl = $"{url.TrimEnd('/')}/?X-Plex-Token={token}";
                var identityResponse = await _httpClient.GetStringAsync(identityUrl, ct);
                var identityXml = XDocument.Parse(identityResponse);
                serverName = identityXml.Root?.Attribute("friendlyName")?.Value;
            }
            catch
            {
                // Ignore - server name is optional
            }

            return new PlexConnectionTestResult
            {
                Connected = true,
                ActiveSessions = sessionCount,
                ServerName = serverName
            };
        }
        catch (HttpRequestException ex)
        {
            return new PlexConnectionTestResult
            {
                Connected = false,
                Error = $"Connection failed: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            return new PlexConnectionTestResult
            {
                Connected = false,
                Error = $"Error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Semantic matching for files with different naming conventions.
    /// Compares normalized show/movie names and episode identifiers.
    /// Examples that should match:
    /// - "DAN DA DAN - S02E03 - You Won't Get Away With This! WEBDL-1080p Proper"
    /// - "DAN.DA.DAN.S02E03.You.Wont.Get.Away.with.This.REPACK.1080p.CR.WEB-DL"
    /// </summary>
    private static bool IsSemanticMatch(string filename1, string filename2)
    {
        // Extract episode identifiers (S01E02 format)
        var episodePattern = new Regex(@"S(\d{1,2})E(\d{1,2})", RegexOptions.IgnoreCase);
        var match1 = episodePattern.Match(filename1);
        var match2 = episodePattern.Match(filename2);

        // If both have episode identifiers, compare them
        if (match1.Success && match2.Success)
        {
            // Episodes must match
            if (!match1.Value.Equals(match2.Value, StringComparison.OrdinalIgnoreCase))
                return false;

            // Extract and normalize show names (everything before the episode identifier)
            var showName1 = NormalizeShowName(filename1[..match1.Index]);
            var showName2 = NormalizeShowName(filename2[..match2.Index]);

            // Show names must be similar (one contains the other or high similarity)
            return AreShowNamesSimilar(showName1, showName2);
        }

        // For movies: try to match by year and normalized title
        var yearPattern = new Regex(@"[\.\s\-_]?((?:19|20)\d{2})[\.\s\-_]?");
        var yearMatch1 = yearPattern.Match(filename1);
        var yearMatch2 = yearPattern.Match(filename2);

        if (yearMatch1.Success && yearMatch2.Success)
        {
            // Years must match
            if (yearMatch1.Groups[1].Value != yearMatch2.Groups[1].Value)
                return false;

            // Extract and normalize movie names (everything before the year)
            var movieName1 = NormalizeShowName(filename1[..yearMatch1.Index]);
            var movieName2 = NormalizeShowName(filename2[..yearMatch2.Index]);

            return AreShowNamesSimilar(movieName1, movieName2);
        }

        return false;
    }

    /// <summary>
    /// Normalize a show/movie name by removing separators and converting to lowercase.
    /// "DAN.DA.DAN" -> "dandadan"
    /// "DAN DA DAN" -> "dandadan"
    /// </summary>
    private static string NormalizeShowName(string name)
    {
        // Remove common separators and convert to lowercase
        var normalized = Regex.Replace(name, @"[\.\s\-_]+", "").ToLowerInvariant();
        // Remove trailing/leading special chars
        normalized = normalized.Trim();
        return normalized;
    }

    /// <summary>
    /// Check if two normalized show names are similar enough to be considered the same.
    /// </summary>
    private static bool AreShowNamesSimilar(string name1, string name2)
    {
        if (string.IsNullOrEmpty(name1) || string.IsNullOrEmpty(name2))
            return false;

        // Exact match after normalization
        if (name1.Equals(name2, StringComparison.OrdinalIgnoreCase))
            return true;

        // One contains the other (handles partial names)
        if (name1.Contains(name2, StringComparison.OrdinalIgnoreCase) ||
            name2.Contains(name1, StringComparison.OrdinalIgnoreCase))
            return true;

        // Check if they share a significant common prefix (at least 70% of shorter name)
        var minLen = Math.Min(name1.Length, name2.Length);
        var commonPrefixLen = 0;
        for (int i = 0; i < minLen; i++)
        {
            if (char.ToLowerInvariant(name1[i]) == char.ToLowerInvariant(name2[i]))
                commonPrefixLen++;
            else
                break;
        }

        return commonPrefixLen >= minLen * 0.7;
    }

    private record ServerSessionResult
    {
        public bool HasActiveSessions { get; init; }
        public int SessionCount { get; init; }
    }
}

public record PlexVerificationResult
{
    public bool IsRealPlayback { get; init; }
    public string Reason { get; init; } = "";
    public int SessionCount { get; init; }
}

public record PlexConnectionTestResult
{
    public bool Connected { get; init; }
    public int? ActiveSessions { get; init; }
    public string? ServerName { get; init; }
    public string? Error { get; init; }
}
