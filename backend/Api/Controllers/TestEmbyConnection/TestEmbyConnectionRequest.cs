using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Api.Controllers.TestEmbyConnection;

public class TestEmbyConnectionRequest
{
    public string Url { get; init; }
    public string ApiKey { get; init; }

    public TestEmbyConnectionRequest(HttpContext context)
    {
        Url = context.Request.Form["url"].FirstOrDefault()
              ?? throw new BadHttpRequestException("Emby URL is required");

        ApiKey = context.Request.Form["apiKey"].FirstOrDefault()
                ?? throw new BadHttpRequestException("Emby API key is required");
    }
}
