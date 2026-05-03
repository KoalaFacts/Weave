using Microsoft.AspNetCore.Mvc;
using Scalar.AspNetCore;
using Weave.ServiceDefaults;
using Weave.Silo.Api;
using Weave.Silo.Configuration;
using Weave.Silo.Security;

namespace Weave.Silo.Startup;

internal sealed class SiloApplicationConfigurator
{
    private readonly WebApplication _app;
    private readonly WeaveSettings _weaveSettings;

    public SiloApplicationConfigurator(WebApplication app, WeaveSettings weaveSettings)
    {
        _app = app;
        _weaveSettings = weaveSettings;
    }

    public async Task ConfigureAsync()
    {
        var authOptions = _app.Services.GetRequiredService<ApiAuthOptions>();
        var auditOptions = _app.Services.GetRequiredService<AuditOptions>();

        if (_weaveSettings.RequireHttps)
            _app.UseHttpsRedirection();

        _app.UseAuditLog(auditOptions);
        _app.UseApiAuth();
        UseProblemDetails();

        await new SiloPluginActivator(_app.Services, _app.Logger, _weaveSettings).ActivateAsync();

        LogRuntimeOptions(authOptions, auditOptions);
        MapEndpoints();
    }

    private void UseProblemDetails()
    {
        _app.UseExceptionHandler(error => error.Run(async context =>
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/problem+json";
            var problem = new ProblemDetails
            {
                Status = 500,
                Title = "Internal Server Error",
                Detail = "An unexpected error occurred."
            };
            await context.Response.WriteAsJsonAsync(problem, SiloApiJsonContext.Default.ProblemDetails);
        }));
    }

    private void LogRuntimeOptions(ApiAuthOptions authOptions, AuditOptions auditOptions)
    {
        if (_weaveSettings.IsLocalMode)
            _app.Logger.LogInformation("Weave running in local mode — no external services required");

        if (authOptions.Provider is not null)
            _app.Logger.LogInformation("API authentication: {Mode}", authOptions.Mode);
        else
            _app.Logger.LogInformation("API authentication: disabled (opt in via Weave:Auth:Mode)");

        if (auditOptions.Enabled)
            _app.Logger.LogInformation("Audit logging: enabled");

        if (_weaveSettings.RequireHttps)
            _app.Logger.LogInformation("HTTPS enforcement: enabled");
    }

    private void MapEndpoints()
    {
        _app.MapDefaultEndpoints();
        _app.MapOpenApi();
        _app.MapScalarApiReference();
        _app.MapWorkspaceEndpoints();
        _app.MapAgentEndpoints();
        _app.MapToolEndpoints();
        _app.MapPluginEndpoints();
        _app.MapSkillEndpoints();
        _app.MapChannelEndpoints();
        _app.MapUserEndpoints();
        _app.MapMarketplaceEndpoints();
        _app.MapTemplateEndpoints();
        _app.MapAuditEndpoints();
    }
}
