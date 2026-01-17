namespace NzbWebDAV.Api.Controllers.GetVersion;

public class GetVersionResponse : BaseApiResponse
{
    public string Version { get; init; } = "dev";
    public string BuildDate { get; init; } = "unknown";
    public string GitBranch { get; init; } = "unknown";
    public string GitCommit { get; init; } = "unknown";
    public string GitRemote { get; init; } = "unknown";
}
