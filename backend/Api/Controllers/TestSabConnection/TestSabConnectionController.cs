using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.TestSabConnection;

[ApiController]
[Route("api/test-sab-connection")]
public class TestSabConnectionController(SabIntegrationService sabService) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new TestSabConnectionRequest(HttpContext);
        var response = await TestConnection(request).ConfigureAwait(false);
        return Ok(response);
    }

    private async Task<TestSabConnectionResponse> TestConnection(TestSabConnectionRequest request)
    {
        try
        {
            var result = await sabService.TestConnection(request.Url, request.ApiKey).ConfigureAwait(false);
            return new TestSabConnectionResponse
            {
                Status = true,
                Connected = result.Connected,
                IsPaused = result.IsPaused,
                Version = result.Version,
                Error = result.Error
            };
        }
        catch (Exception e)
        {
            return new TestSabConnectionResponse
            {
                Status = true,
                Connected = false,
                Error = e.Message
            };
        }
    }
}
