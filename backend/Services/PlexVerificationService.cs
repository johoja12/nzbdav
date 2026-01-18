using System.Text.RegularExpressions;
using System.Xml.Linq;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Verifies if NZBDav streaming is for actual user playback by checking Plex sessions.
/// Plex /status/sessions only shows real user playback - not intro detection, thumbnails, etc.
/// </summary>
public class PlexVerificationService
{
    private readonly ConfigManager _configManager;
    private readonly HttpClient _httpClient;

    // Cache for active session file names (refreshed periodically)
    private HashSet<string> _activeSessionFiles = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _cacheLastUpdated = DateTime.MinValue;
    private readonly object _cacheLock = new();
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Static instance for access from non-DI contexts (e.g., NzbFileStream).
    /// Set automatically when the service is created via DI.
    /// </summary>
    public static PlexVerificationService? Instance { get; private set; }

    public PlexVerificationService(ConfigManager configManager)
    {
        _configManager = configManager;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        Instance = this; // Set static instance for non-DI access
    }

    /// <summary>
    /// Check if a specific file is currently being played in an active Plex session.
    /// Uses cached session data (refreshed every 3 seconds) for fast lookups.
    /// </summary>
    /// <param name="filePath">Full path or filename to check</param>
    /// <returns>True if file is in an active Plex playback session</returns>
    public bool IsFilePlaying(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        var config = _configManager.GetPlexConfig();
        if (!config.VerifyPlayback || config.Servers.Count == 0)
        {
            // If verification disabled, assume all streaming is real playback
            return true;
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

        // If cache had sessions but file wasn't found, this could be a race condition
        // where Plex hasn't registered the new playback yet. Force an immediate refresh.
        if (hadSessions)
        {
            Log.Debug("[PlexVerify] IsFilePlaying({File}): Cache miss with {Count} active sessions, forcing refresh",
                filename, _activeSessionFiles.Count);
            ForceRefreshCache();

            // Check again after fresh refresh
            var (foundAfterRefresh, _) = CheckFileInCache(filename, filenameWithoutExt);
            if (foundAfterRefresh)
                return true;

            // Still not found after fresh refresh - but Plex session registration can be slow.
            // Default to PlexPlayback (true) to avoid penalizing real playback that just started.
            // Background activity (thumbnails, intro detection) is typically short-lived anyway.
            Log.Debug("[PlexVerify] IsFilePlaying({File}): True (not in cache but defaulting to playback - Plex may be slow to register)",
                filename);
            return true;
        }

        // No sessions in cache - assume real playback (safe default)
        Log.Debug("[PlexVerify] IsFilePlaying({File}): True (no active sessions, assuming real playback)", filename);
        return true;
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
                _cacheLastUpdated = DateTime.UtcNow;
            }
            return;
        }

        var allFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var server in enabledServers)
        {
            try
            {
                var files = await GetServerSessionFilesAsync(server);
                foreach (var file in files)
                {
                    allFiles.Add(file);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("[PlexVerify] Failed to get sessions from {Server}: {Error}", server.Name, ex.Message);
            }
        }

        lock (_cacheLock)
        {
            _activeSessionFiles = allFiles;
            _cacheLastUpdated = DateTime.UtcNow;
        }

        if (allFiles.Count > 0)
        {
            Log.Debug("[PlexVerify] Cache refreshed with {Count} active files: {Files}",
                allFiles.Count, string.Join(", ", allFiles.Take(5)));
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
