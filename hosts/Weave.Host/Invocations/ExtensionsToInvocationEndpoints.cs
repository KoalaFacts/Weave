using Microsoft.Extensions.Options;
using Weave.Security.Tokens;

namespace Weave.Silo.Invocations;

public static class ExtensionsToInvocationEndpoints
{
    private const string DevelopmentSigningKey = "weave-development-signing-key-change-me";

    public static void MapGovernedInvocationEndpoints(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Weave:Invocations:Http:Enabled"))
            return;

        var keys = app.Services.GetRequiredService<IOptions<CapabilityTokenOptions>>().Value;
        if (keys.SigningKey == DevelopmentSigningKey || keys.PreviousSigningKey == DevelopmentSigningKey)
            throw new InvalidOperationException("The governed HTTP entry cannot use the public development signing key, including as a previous key.");
        _ = app.Services.GetRequiredService<ICapabilityTokenService>();

        var group = app.MapGroup("/api/workspaces/{workspaceId}/tools/{toolName}/invocations")
            .WithTags("Governed Invocations");
        group.MapPost("", InvokeToolEndpoint.HandleAsync);
        group.MapGet("/{invocationId}", GetInvocationEndpoint.HandleAsync);
        group.MapGet("/{invocationId}/approval", GetApprovalEndpoint.HandleAsync);
        group.MapPost("/{invocationId}/approval/review", ReviewApprovalEndpoint.HandleAsync);
    }
}
