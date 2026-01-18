using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Api.Controllers.TestPlexConnection;

public class TestPlexConnectionRequest
{
    public string Url { get; init; }
    public string Token { get; init; }

    public TestPlexConnectionRequest(HttpContext context)
    {
        Url = context.Request.Form["url"].FirstOrDefault()
              ?? throw new BadHttpRequestException("Plex URL is required");

        Token = context.Request.Form["token"].FirstOrDefault()
                ?? throw new BadHttpRequestException("Plex token is required");
    }
}
