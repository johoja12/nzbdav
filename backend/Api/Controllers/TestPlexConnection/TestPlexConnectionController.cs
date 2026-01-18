using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.TestPlexConnection;

[ApiController]
[Route("api/test-plex-connection")]
public class TestPlexConnectionController(PlexVerificationService plexService) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new TestPlexConnectionRequest(HttpContext);
        var response = await TestConnection(request).ConfigureAwait(false);
        return Ok(response);
    }

    private async Task<TestPlexConnectionResponse> TestConnection(TestPlexConnectionRequest request)
    {
        try
        {
            var result = await plexService.TestConnection(request.Url, request.Token).ConfigureAwait(false);
            return new TestPlexConnectionResponse
            {
                Status = true,
                Connected = result.Connected,
                ActiveSessions = result.ActiveSessions,
                ServerName = result.ServerName,
                Error = result.Error
            };
        }
        catch (Exception e)
        {
            return new TestPlexConnectionResponse
            {
                Status = true,
                Connected = false,
                Error = e.Message
            };
        }
    }
}
