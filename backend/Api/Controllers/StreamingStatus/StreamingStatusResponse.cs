namespace NzbWebDAV.Api.Controllers.StreamingStatus;

public class StreamingStatusResponse : BaseApiResponse
{
    public int ActiveStreams { get; init; }
    public bool SabPausedByNzbdav { get; init; }
    public DateTimeOffset? SabPausedAt { get; init; }
    public bool SabAutoPauseEnabled { get; init; }
    public bool PlexVerifyEnabled { get; init; }
    public bool StreamingMonitorEnabled { get; init; }
}
