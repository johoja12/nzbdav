using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Monitors streaming activity and coordinates SABnzbd pause/resume
/// based on verified Plex or Emby playback.
/// </summary>
public class StreamingMonitorService : IHostedService, IDisposable
{
    private readonly ConfigManager _configManager;
    private readonly PlexVerificationService _plexVerificationService;
    private readonly EmbyVerificationService _embyVerificationService;
    private readonly SabIntegrationService _sabIntegrationService;
    private readonly WebhookService _webhookService;
    private readonly UsenetStreamingClient _usenetClient;

    private Timer? _debounceTimer;
    private Timer? _pollTimer;
    private readonly object _lock = new();
    private int _activeStreamCount;
    private bool _isStreamingActive;
    private bool _startDebounceInProgress;
    private DateTime? _streamingStartedAt;
    private CancellationTokenSource? _cts;

    public StreamingMonitorService(
        ConfigManager configManager,
        PlexVerificationService plexVerificationService,
        EmbyVerificationService embyVerificationService,
        SabIntegrationService sabIntegrationService,
        WebhookService webhookService,
        UsenetStreamingClient usenetClient)
    {
        _configManager = configManager;
        _plexVerificationService = plexVerificationService;
        _embyVerificationService = embyVerificationService;
        _sabIntegrationService = sabIntegrationService;
        _webhookService = webhookService;
        _usenetClient = usenetClient;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var config = _configManager.GetStreamingMonitorConfig();
        if (config.Enabled)
        {
            // Subscribe to connection pool changes if available
            var connectionPoolStats = _usenetClient.ConnectionPoolStats;
            if (connectionPoolStats != null)
            {
                connectionPoolStats.OnStreamingChanged += HandleStreamingChanged;
                Log.Warning("StreamingMonitorService started - auto-pause enabled, subscribed to events");
            }
            else
            {
                // Fall back to polling if not yet initialized
                _pollTimer = new Timer(PollStreamingState, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));
                Log.Warning("StreamingMonitorService started - auto-pause enabled, using polling mode");
            }
        }
        else
        {
            Log.Warning("StreamingMonitorService started - auto-pause disabled");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        var connectionPoolStats = _usenetClient.ConnectionPoolStats;
        if (connectionPoolStats != null)
        {
            connectionPoolStats.OnStreamingChanged -= HandleStreamingChanged;
        }
        _debounceTimer?.Dispose();
        _pollTimer?.Dispose();
        _cts?.Cancel();

        Log.Warning("StreamingMonitorService stopped");
        return Task.CompletedTask;
    }

    private void PollStreamingState(object? state)
    {
        var connectionPoolStats = _usenetClient.ConnectionPoolStats;
        if (connectionPoolStats == null) return;

        // Check if we can switch to event mode
        connectionPoolStats.OnStreamingChanged += HandleStreamingChanged;
        _pollTimer?.Dispose();
        _pollTimer = null;
        Log.Debug("StreamingMonitorService switched to event mode");
    }

    private void HandleStreamingChanged(object? sender, StreamingChangedEventArgs args)
    {
        var config = _configManager.GetStreamingMonitorConfig();
        if (!config.Enabled) return;

        lock (_lock)
        {
            _activeStreamCount = args.ActiveStreamCount;

            // Start streaming: 0 -> n (only if not already in debounce or active)
            if (args.ActiveStreamCount > 0 && !_isStreamingActive && !_startDebounceInProgress)
            {
                _startDebounceInProgress = true;
                var debounceMs = config.StartDebounceSeconds * 1000;
                _debounceTimer?.Dispose();
                _debounceTimer = new Timer(
                    _ => _ = OnStreamingStartedDebounced(),
                    null,
                    debounceMs,
                    Timeout.Infinite);

                Log.Warning("Streaming detected ({Count} streams), will verify in {Seconds}s",
                    args.ActiveStreamCount, config.StartDebounceSeconds);
            }
            // Stop streaming: only when already actively streaming (not during start debounce)
            else if (args.ActiveStreamCount == 0 && _isStreamingActive)
            {
                // Start stop debounce
                _debounceTimer?.Dispose();
                var debounceMs = config.StopDebounceSeconds * 1000;
                _debounceTimer = new Timer(
                    _ => _ = OnStreamingStoppedDebounced(),
                    null,
                    debounceMs,
                    Timeout.Infinite);

                Log.Warning("Streaming stopped, will resume in {Seconds}s", config.StopDebounceSeconds);
            }
            // Brief drops to 0 during start debounce: ignore (let timer check count)
            // Count changes while streaming (n -> m where both > 0): ignore (Plex checked on stop)
        }
    }

    private async Task OnStreamingStartedDebounced()
    {
        try
        {
            // Verify still streaming after debounce
            lock (_lock)
            {
                _startDebounceInProgress = false;
                if (_activeStreamCount == 0) return;
            }

            // Check if this is real playback from EITHER Plex OR Emby
            var plexConfig = _configManager.GetPlexConfig();
            var embyConfig = _configManager.GetEmbyConfig();

            bool isRealPlayback = false;

            if (plexConfig.VerifyPlayback)
            {
                isRealPlayback = await _plexVerificationService.IsAnyServerPlaying().ConfigureAwait(false);
            }

            if (!isRealPlayback && embyConfig.VerifyPlayback)
            {
                isRealPlayback = await _embyVerificationService.IsAnyServerPlaying().ConfigureAwait(false);
            }

            // If neither verification is enabled, assume real playback
            if (!plexConfig.VerifyPlayback && !embyConfig.VerifyPlayback)
            {
                isRealPlayback = true;
            }

            if (!isRealPlayback)
            {
                Log.Warning("Stream detected but no Plex/Emby playback - skipping SAB pause (likely intro detection or thumbnail generation)");
                return;
            }

            lock (_lock)
            {
                if (_isStreamingActive) return; // Already handled
                _isStreamingActive = true;
                _streamingStartedAt = DateTime.UtcNow;
            }

            Log.Warning("Verified playback started - pausing SABnzbd");

            // Fire webhook
            await _webhookService.FireEventAsync("streaming.started", new
            {
                timestamp = DateTime.UtcNow,
                activeStreams = _activeStreamCount
            }).ConfigureAwait(false);

            // Pause SABnzbd
            var sabConfig = _configManager.GetSabPauseConfig();
            if (sabConfig.AutoPause)
            {
                await _sabIntegrationService.PauseAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling streaming started event");
        }
    }

    private async Task OnStreamingStoppedDebounced()
    {
        try
        {
            // Check if EITHER Plex or Emby still has active playback
            var plexConfig = _configManager.GetPlexConfig();
            var embyConfig = _configManager.GetEmbyConfig();

            bool isStillPlaying = false;

            if (plexConfig.VerifyPlayback)
            {
                isStillPlaying = await _plexVerificationService.IsAnyServerPlaying().ConfigureAwait(false);
            }

            if (!isStillPlaying && embyConfig.VerifyPlayback)
            {
                isStillPlaying = await _embyVerificationService.IsAnyServerPlaying().ConfigureAwait(false);
            }

            if (isStillPlaying)
            {
                Log.Debug("Plex/Emby still showing active playback, not resuming SABnzbd");
                return;
            }

            // If neither verification is enabled, fall back to stream count check
            if (!plexConfig.VerifyPlayback && !embyConfig.VerifyPlayback)
            {
                lock (_lock)
                {
                    if (_activeStreamCount > 0) return;
                }
            }

            DateTime? startedAt;
            lock (_lock)
            {
                if (!_isStreamingActive) return; // Already handled
                _isStreamingActive = false;
                startedAt = _streamingStartedAt;
                _streamingStartedAt = null;
            }

            var duration = startedAt.HasValue
                ? DateTime.UtcNow - startedAt.Value
                : TimeSpan.Zero;

            Log.Warning("Playback stopped after {Duration:F1}s - resuming SABnzbd", duration.TotalSeconds);

            // Fire webhook
            await _webhookService.FireEventAsync("streaming.stopped", new
            {
                timestamp = DateTime.UtcNow,
                durationSeconds = duration.TotalSeconds
            }).ConfigureAwait(false);

            // Resume SABnzbd
            var sabConfig = _configManager.GetSabPauseConfig();
            if (sabConfig.AutoPause)
            {
                await _sabIntegrationService.ResumeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling streaming stopped event");
        }
    }

    public void Dispose()
    {
        _debounceTimer?.Dispose();
        _pollTimer?.Dispose();
        _cts?.Dispose();
    }
}
