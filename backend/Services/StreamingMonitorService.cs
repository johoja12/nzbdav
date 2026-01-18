using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Monitors streaming activity and coordinates SABnzbd pause/resume
/// based on verified Plex playback.
/// </summary>
public class StreamingMonitorService : IHostedService, IDisposable
{
    private readonly ConfigManager _configManager;
    private readonly PlexVerificationService _plexVerificationService;
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
        SabIntegrationService sabIntegrationService,
        WebhookService webhookService,
        UsenetStreamingClient usenetClient)
    {
        _configManager = configManager;
        _plexVerificationService = plexVerificationService;
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

            // Check if this is real Plex playback
            var plexConfig = _configManager.GetPlexConfig();
            if (plexConfig.VerifyPlayback)
            {
                var isRealPlayback = await _plexVerificationService.IsAnyServerPlaying();
                if (!isRealPlayback)
                {
                    Log.Warning("Stream detected but no Plex playback - skipping SAB pause (likely intro detection or thumbnail generation)");
                    return;
                }
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
            });

            // Pause SABnzbd
            var sabConfig = _configManager.GetSabPauseConfig();
            if (sabConfig.AutoPause)
            {
                await _sabIntegrationService.PauseAsync();
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
            // Check if Plex still has active playback (more reliable than stream count)
            var plexConfig = _configManager.GetPlexConfig();
            if (plexConfig.VerifyPlayback)
            {
                var isStillPlaying = await _plexVerificationService.IsAnyServerPlaying();
                if (isStillPlaying)
                {
                    Log.Debug("Plex still showing active playback, not resuming SABnzbd");
                    return;
                }
            }
            else
            {
                // Fall back to stream count check if Plex verification disabled
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
            });

            // Resume SABnzbd
            var sabConfig = _configManager.GetSabPauseConfig();
            if (sabConfig.AutoPause)
            {
                await _sabIntegrationService.ResumeAsync();
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
