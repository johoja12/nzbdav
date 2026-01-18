using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Api.Controllers.TestSabConnection;

public class TestSabConnectionRequest
{
    public string Url { get; init; }
    public string ApiKey { get; init; }

    public TestSabConnectionRequest(HttpContext context)
    {
        Url = context.Request.Form["url"].FirstOrDefault()
              ?? throw new BadHttpRequestException("SABnzbd URL is required");

        ApiKey = context.Request.Form["apiKey"].FirstOrDefault()
                 ?? throw new BadHttpRequestException("SABnzbd API key is required");
    }
}
