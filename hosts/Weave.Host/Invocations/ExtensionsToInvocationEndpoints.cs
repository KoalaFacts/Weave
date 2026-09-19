namespace Weave.Silo.Invocations;

public static class ExtensionsToInvocationEndpoints
{
    public static void MapGovernedInvocationEndpoints(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Weave:Invocations:Http:Enabled"))
            return;

        var group = app.MapGroup("/api/workspaces/{workspaceId}/tools/{toolName}/invocations")
            .WithTags("Governed Invocations");
        group.MapPost("", InvokeToolEndpoint.HandleAsync);
        group.MapGet("/{invocationId}", GetInvocationEndpoint.HandleAsync);
        group.MapGet("/{invocationId}/approval", GetApprovalEndpoint.HandleAsync);
    }
}
