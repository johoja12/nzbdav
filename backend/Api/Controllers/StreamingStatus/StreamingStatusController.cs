using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.StreamingStatus;

[ApiController]
[Route("api/streaming/status")]
public class StreamingStatusController(
    ConfigManager configManager,
    UsenetStreamingClient usenetClient,
    SabIntegrationService sabService) : BaseApiController
{
    protected override Task<IActionResult> HandleRequest()
    {
        var streamingConfig = configManager.GetStreamingMonitorConfig();
        var plexConfig = configManager.GetPlexConfig();
        var sabConfig = configManager.GetSabPauseConfig();

        var connectionPoolStats = usenetClient.ConnectionPoolStats;
        var activeStreams = connectionPoolStats?.GetStreamingConnectionCount() ?? 0;

        var response = new StreamingStatusResponse
        {
            Status = true,
            ActiveStreams = activeStreams,
            SabPausedByNzbdav = sabService.IsPausedByUs,
            SabPausedAt = sabService.PausedAt,
            SabAutoPauseEnabled = sabConfig.AutoPause,
            PlexVerifyEnabled = plexConfig.VerifyPlayback,
            StreamingMonitorEnabled = streamingConfig.Enabled
        };

        return Task.FromResult<IActionResult>(Ok(response));
    }
}
