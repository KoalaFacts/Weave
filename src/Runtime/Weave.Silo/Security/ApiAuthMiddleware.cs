using Microsoft.AspNetCore.Mvc;
using Weave.Silo.Api;

namespace Weave.Silo.Security;

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
        if (options.Mode == ApiAuthMode.None)
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

        var authenticated = options.Mode switch
        {
            ApiAuthMode.ApiKey => ValidateApiKey(context),
            ApiAuthMode.Bearer => ValidateBearer(context),
            _ => true
        };

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
                Detail = options.Mode == ApiAuthMode.ApiKey
                    ? "Missing or invalid API key. Provide it via X-Api-Key header."
                    : "Missing or invalid bearer token. Provide it via Authorization: Bearer <token> header."
            }, SiloApiJsonContext.Default.ProblemDetails);
            return;
        }

        await next(context);
    }

    private bool ValidateApiKey(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue("X-Api-Key", out var key))
            return false;

        return string.Equals(key.ToString(), options.ResolvedSecret, StringComparison.Ordinal);
    }

    private bool ValidateBearer(HttpContext context)
    {
        var auth = context.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;

        var token = auth["Bearer ".Length..].Trim();
        return string.Equals(token, options.ResolvedSecret, StringComparison.Ordinal);
    }
}

public enum ApiAuthMode
{
    None,
    ApiKey,
    Bearer
}

public sealed class ApiAuthOptions
{
    public const string ConfigSection = "Weave:Auth";

    public ApiAuthMode Mode { get; set; } = ApiAuthMode.None;
    public string? Secret { get; set; }

    internal string? ResolvedSecret { get; set; }

    public static ApiAuthOptions FromConfiguration(IConfiguration configuration)
    {
        var modeStr = configuration[$"{ConfigSection}:Mode"];
        var secret = configuration[$"{ConfigSection}:Secret"];

        var mode = ApiAuthMode.None;
        if (!string.IsNullOrWhiteSpace(modeStr))
            Enum.TryParse(modeStr, ignoreCase: true, out mode);

        string? resolved = null;
        if (mode != ApiAuthMode.None && !string.IsNullOrWhiteSpace(secret))
            resolved = ResolveSecret(secret);

        return new ApiAuthOptions
        {
            Mode = mode,
            Secret = secret,
            ResolvedSecret = resolved
        };
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
        if (options.Mode != ApiAuthMode.None)
        {
            app.UseMiddleware<ApiAuthMiddleware>();
        }

        return app;
    }
}
