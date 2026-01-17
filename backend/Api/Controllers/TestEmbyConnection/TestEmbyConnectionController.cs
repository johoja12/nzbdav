using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.TestEmbyConnection;

[ApiController]
[Route("api/test-emby-connection")]
public class TestEmbyConnectionController(EmbyVerificationService embyService) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new TestEmbyConnectionRequest(HttpContext);
        var response = await TestConnection(request).ConfigureAwait(false);
        return Ok(response);
    }

    private async Task<TestEmbyConnectionResponse> TestConnection(TestEmbyConnectionRequest request)
    {
        try
        {
            var result = await embyService.TestConnection(request.Url, request.ApiKey).ConfigureAwait(false);
            return new TestEmbyConnectionResponse
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
            return new TestEmbyConnectionResponse
            {
                Status = true,
                Connected = false,
                Error = e.Message
            };
        }
    }
}
