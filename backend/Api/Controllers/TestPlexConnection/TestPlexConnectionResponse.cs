namespace NzbWebDAV.Api.Controllers.TestPlexConnection;

public class TestPlexConnectionResponse : BaseApiResponse
{
    public bool Connected { get; init; }
    public int? ActiveSessions { get; init; }
    public new string? Error { get; init; }
    public string? ServerName { get; init; }
}
