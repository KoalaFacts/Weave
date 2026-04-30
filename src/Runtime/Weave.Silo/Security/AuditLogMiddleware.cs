using System.Diagnostics;

namespace Weave.Silo.Security;

public sealed class AuditLogMiddleware(RequestDelegate next, ILogger<AuditLogMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var sw = Stopwatch.StartNew();
        var method = context.Request.Method;
        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var apiKey = context.Request.Headers["X-Api-Key"].FirstOrDefault();
        var caller = !string.IsNullOrWhiteSpace(apiKey) ? $"apikey:{apiKey[..Math.Min(8, apiKey.Length)]}***" : "anonymous";

        try
        {
            await next(context);
        }
        finally
        {
            sw.Stop();
            var status = context.Response.StatusCode;

            logger.LogInformation(
                "AUDIT {Method} {Path} -> {Status} | caller={Caller} ip={RemoteIp} duration={Duration}ms",
                method,
                path,
                status,
                caller,
                remoteIp,
                sw.ElapsedMilliseconds);
        }
    }
}


