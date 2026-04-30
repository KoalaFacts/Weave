namespace Weave.Silo.Security;

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
