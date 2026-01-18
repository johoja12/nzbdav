using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Serilog;

namespace NzbWebDAV.Middleware;

public class WebDavWriteLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private static readonly HashSet<string> WriteMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "PUT", "DELETE", "MKCOL", "PROPPATCH", "COPY", "MOVE"
    };

    public WebDavWriteLoggingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var method = context.Request.Method;

        if (WriteMethods.Contains(method))
        {
            var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var path = context.Request.Path;
            var userAgent = context.Request.Headers.UserAgent.ToString();

            Log.Warning("[WebDAV Write] {Method} {Path} from {ClientIp} (UA: {UserAgent})",
                method, path, clientIp, userAgent);
        }

        await _next(context);
    }
}

public static class WebDavWriteLoggingMiddlewareExtensions
{
    public static IApplicationBuilder UseWebDavWriteLogging(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<WebDavWriteLoggingMiddleware>();
    }
}
