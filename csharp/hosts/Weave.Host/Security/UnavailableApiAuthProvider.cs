namespace Weave.Silo.Security;

internal sealed class UnavailableApiAuthProvider : IApiAuthProvider
{
    public string Name => "unavailable";
    public string UnauthorizedMessage => "Authentication is unavailable. An administrator must restore the configured provider.";

    public Task<bool> AuthenticateAsync(HttpContext context) => Task.FromResult(false);
}
