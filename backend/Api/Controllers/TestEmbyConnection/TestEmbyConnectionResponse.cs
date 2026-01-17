namespace NzbWebDAV.Api.Controllers.TestEmbyConnection;

public class TestEmbyConnectionResponse : BaseApiResponse
{
    public bool Connected { get; init; }
    public int? ActiveSessions { get; init; }
    public new string? Error { get; init; }
    public string? ServerName { get; init; }
}
