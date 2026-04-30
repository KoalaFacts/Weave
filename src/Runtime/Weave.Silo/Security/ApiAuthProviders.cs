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
