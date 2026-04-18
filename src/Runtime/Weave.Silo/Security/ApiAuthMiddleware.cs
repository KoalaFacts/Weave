using Microsoft.AspNetCore.Mvc;
using Weave.Silo.Api;

namespace Weave.Silo.Security;

public interface IApiAuthProvider
{
    string Name { get; }
    Task<bool> AuthenticateAsync(HttpContext context);
    string UnauthorizedMessage { get; }
}

public sealed class ApiKeyAuthProvider(string resolvedSecret) : IApiAuthProvider
{
    public string Name => "apikey";
    public string UnauthorizedMessage => "Missing or invalid API key. Provide it via X-Api-Key header.";

    public Task<bool> AuthenticateAsync(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue("X-Api-Key", out var key))
            return Task.FromResult(false);

        return Task.FromResult(string.Equals(key.ToString(), resolvedSecret, StringComparison.Ordinal));
    }
}

public sealed class BearerAuthProvider(string resolvedSecret) : IApiAuthProvider
{
    public string Name => "bearer";
    public string UnauthorizedMessage => "Missing or invalid bearer token. Provide it via Authorization: Bearer <token> header.";

    public Task<bool> AuthenticateAsync(HttpContext context)
    {
        var auth = context.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(false);

        var token = auth["Bearer ".Length..].Trim();
        return Task.FromResult(string.Equals(token, resolvedSecret, StringComparison.Ordinal));
    }
}

public sealed class ApiAuthMiddleware(RequestDelegate next, ApiAuthOptions options, ILogger<ApiAuthMiddleware> logger)
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
        if (options.Provider is null)
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

        var authenticated = await options.Provider.AuthenticateAsync(context);

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
                Detail = options.Provider.UnauthorizedMessage
            }, SiloApiJsonContext.Default.ProblemDetails);
            return;
        }

        await next(context);
    }
}

public sealed class ApiAuthOptions
{
    public const string ConfigSection = "Weave:Auth";

    public IApiAuthProvider? Provider { get; set; }
    public string Mode { get; set; } = "none";

    public static ApiAuthOptions FromConfiguration(IConfiguration configuration)
    {
        var mode = configuration[$"{ConfigSection}:Mode"]?.ToLowerInvariant() ?? "none";
        var secret = configuration[$"{ConfigSection}:Secret"];

        string? resolved = null;
        if (mode != "none" && !string.IsNullOrWhiteSpace(secret))
            resolved = ResolveSecret(secret);

        IApiAuthProvider? provider = mode switch
        {
            "apikey" when resolved is not null => new ApiKeyAuthProvider(resolved),
            "bearer" when resolved is not null => new BearerAuthProvider(resolved),
            _ => null
        };

        return new ApiAuthOptions { Provider = provider, Mode = mode };
    }

    private static string? ResolveSecret(string reference)
    {
        if (reference.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var varName = reference[4..].Trim();
            return Environment.GetEnvironmentVariable(varName);
        }

        if (reference.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var path = reference[5..].Trim();
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }

        return reference;
    }
}

public static class ApiAuthExtensions
{
    public static IApplicationBuilder UseApiAuth(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<ApiAuthOptions>();
        if (options.Provider is not null)
        {
            app.UseMiddleware<ApiAuthMiddleware>();
        }

        return app;
    }
}
