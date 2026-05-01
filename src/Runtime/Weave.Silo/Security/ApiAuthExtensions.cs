namespace Weave.Silo.Security;

public static class ApiAuthExtensions
{
    public static IApplicationBuilder UseApiAuth(this IApplicationBuilder app)
    {
        app.UseMiddleware<ApiAuthMiddleware>();
        return app;
    }
}