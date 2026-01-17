using Microsoft.AspNetCore.Mvc;

namespace NzbWebDAV.Api.Controllers.GetVersion;

[ApiController]
[Route("api/version")]
public class GetVersionController : BaseApiController
{
    // Version endpoint should be public (no auth required)
    protected override bool RequiresAuthentication => false;

    protected override Task<IActionResult> HandleRequest()
    {
        var response = new GetVersionResponse
        {
            Version = Environment.GetEnvironmentVariable("NZBDAV_VERSION") ?? "dev",
            BuildDate = Environment.GetEnvironmentVariable("NZBDAV_BUILD_DATE") ?? "unknown",
            GitBranch = Environment.GetEnvironmentVariable("NZBDAV_GIT_BRANCH") ?? "unknown",
            GitCommit = Environment.GetEnvironmentVariable("NZBDAV_GIT_COMMIT") ?? "unknown",
            GitRemote = Environment.GetEnvironmentVariable("NZBDAV_GIT_REMOTE") ?? "unknown"
        };

        return Task.FromResult<IActionResult>(Ok(response));
    }
}
