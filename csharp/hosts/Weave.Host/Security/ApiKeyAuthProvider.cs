namespace Weave.Silo.Security;

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
