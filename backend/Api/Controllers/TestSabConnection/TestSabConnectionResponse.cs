namespace NzbWebDAV.Api.Controllers.TestSabConnection;

public class TestSabConnectionResponse : BaseApiResponse
{
    public bool Connected { get; init; }
    public bool? IsPaused { get; init; }
    public string? Version { get; init; }
    public new string? Error { get; init; }
}
