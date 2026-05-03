namespace Weave.Silo.Security;

public interface IApiAuthProvider
{
    string Name { get; }
    Task<bool> AuthenticateAsync(HttpContext context);
    string UnauthorizedMessage { get; }
}
