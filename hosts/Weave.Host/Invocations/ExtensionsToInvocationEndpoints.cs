using Microsoft.Extensions.Options;
using Weave.Security.Tokens;

namespace Weave.Silo.Invocations;

public static class ExtensionsToInvocationEndpoints
{
    private const string DevelopmentSigningKey = "weave-development-signing-key-change-me";

    public static void MapGovernedInvocationEndpoints(this WebApplication app)
    {
        var enabled = app.Configuration.GetValue<bool>("Weave:Invocations:Http:Enabled");
        var agentOnly = app.Configuration.GetValue<bool>("Weave:Invocations:Http:AgentOnly");
        var decisionsEnabled = app.Configuration.GetValue<bool>("Weave:Invocations:Http:DecisionsEnabled");
        if (agentOnly && (!enabled || decisionsEnabled))
            throw new InvalidOperationException("AgentOnly requires governed HTTP Enabled=true and DecisionsEnabled=false.");
        if (!enabled)
            return;

        var keys = app.Services.GetRequiredService<IOptions<CapabilityTokenOptions>>().Value;
        if (keys.SigningKey == DevelopmentSigningKey || keys.PreviousSigningKey == DevelopmentSigningKey)
            throw new InvalidOperationException("The governed HTTP entry cannot use the public development signing key, including as a previous key.");
        _ = app.Services.GetRequiredService<ICapabilityTokenService>();

        var group = app.MapGroup("/api/workspaces/{workspaceId}/tools/{toolName}/invocations")
            .WithTags("Governed Invocations");
        group.MapPost("", InvokeToolEndpoint.HandleAsync).WithMetadata(new AgentInvocationEndpoint());
        group.MapGet("/{invocationId}", GetInvocationEndpoint.HandleAsync).WithMetadata(new AgentInvocationEndpoint());
        group.MapGet("/{invocationId}/approval", GetApprovalEndpoint.HandleAsync).WithMetadata(new AgentInvocationEndpoint());
        if (agentOnly)
            return;

        group.MapPost("/{invocationId}/approval/review", ReviewApprovalEndpoint.HandleAsync);
        if (decisionsEnabled)
            group.MapPost("/{invocationId}/approval/decision", DecideReviewedApprovalEndpoint.HandleAsync);
    }
}
