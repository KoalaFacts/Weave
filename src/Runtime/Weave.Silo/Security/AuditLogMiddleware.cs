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

public sealed class AuditOptions
{
    public const string ConfigSection = "Weave:Audit";

    public bool Enabled { get; set; }

    public static AuditOptions FromConfiguration(IConfiguration configuration)
    {
        var disabled = configuration.GetValue<bool>($"{ConfigSection}:Disabled");
        return new AuditOptions { Enabled = !disabled };
    }
}

public static class AuditExtensions
{
    public static IApplicationBuilder UseAuditLog(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<AuditOptions>();
        if (options.Enabled)
        {
            app.UseMiddleware<AuditLogMiddleware>();
        }

        return app;
    }
}
