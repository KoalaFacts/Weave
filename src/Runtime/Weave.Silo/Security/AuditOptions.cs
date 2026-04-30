namespace Weave.Silo.Security;

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
    // Options are passed explicitly so startup code doesn't reach into
    // the root IServiceProvider for configuration — that pattern masks
    // scope issues the moment someone registers request-scoped audit
    // state. See docs/best-practices.md (Middleware resolves per-request
    // services from HttpContext.RequestServices).
    public static IApplicationBuilder UseAuditLog(this IApplicationBuilder app, AuditOptions options)
    {
        if (options.Enabled)
            app.UseMiddleware<AuditLogMiddleware>();

        return app;
    }
}
