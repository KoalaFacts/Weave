using Microsoft.AspNetCore.Mvc;
using Weave.Silo.Api;

namespace Weave.Silo.Security;

public sealed class ApiAuthMiddleware(
    RequestDelegate next,
    Weave.Shared.Plugins.PluginServiceBroker broker,
    ApiAuthOptions options,
    ILogger<ApiAuthMiddleware> logger)
{
    private static readonly HashSet<string> BypassPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/health",
        "/alive",
        "/openapi",
        "/scalar"
    };

    public async Task InvokeAsync(HttpContext context)
    {
        var provider = broker.Get<IApiAuthProvider>() ?? options.Provider;

        if (provider is null)
        {
            await next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "";

        if (BypassPaths.Any(bp => path.StartsWith(bp, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var authenticated = await provider.AuthenticateAsync(context);

        if (!authenticated)
        {
            logger.LogWarning("Unauthorized API request: {Method} {Path} from {RemoteIp}",
                context.Request.Method, path, context.Connection.RemoteIpAddress);

            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = 401,
                Title = "Unauthorized",
                Detail = provider.UnauthorizedMessage
            }, SiloApiJsonContext.Default.ProblemDetails);
            return;
        }

        await next(context);
    }
}

