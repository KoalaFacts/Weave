using Weave.Security.Tokens;
using Weave.Shared.Ids;
using Weave.Shared.VirtualActors;
using Weave.Silo.Api;
using Weave.Silo.Invocations;
using Weave.Silo.Management;
using Weave.Silo.Plugins;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Runtime;
using Weave.Workspaces.RuntimeRecovery;

namespace Weave.Silo.RuntimeRecovery;

public static class WorkspaceRuntimeEndpoints
{
    public static RouteGroupBuilder Map(RouteGroupBuilder routes)
    {
        routes.MapGet("/{workspaceId}/runtime", ObserveAsync)
            .WithDescription("Observe retained runtime resources and derive readiness with reasons; registration alone is not readiness.");
        routes.MapPost("/{workspaceId}/containers/{containerId}/recover", RecoverAsync)
            .WithDescription("Start one retained stopped container. Does not restore Agent state or replay tool invocations.");
        return routes;
    }

    private static async Task<IResult> ObserveAsync(string workspaceId, HttpContext context,
        IPluginInstallationAuthority authority, IVirtualActorProvider actors, ICapabilityTokenService tokens, TimeProvider timeProvider,
        CancellationToken ct)
    {
        var denial = await authority.DenialAsync(context, workspaceId, WorkspaceRuntimeRecovery.ReadGrant);
        if (denial is not null)
            return denial;
        if (!InvocationHttp.TryReadCapability(context, tokens, out var token, out var failure))
            return failure;
        var workspace = actors.GetActor<IWorkspaceActor>(VirtualActorId.From(workspaceId));
        if ((await workspace.GetStateAsync()).WorkspaceId.IsEmpty)
            return InvocationHttp.Error(404, "workspace-not-found");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10), timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return Results.Ok(await workspace.ObserveRuntimeAsync(token, linked.Token));
        }
        catch (UnauthorizedAccessException)
        {
            return InvocationHttp.Error(403, "forbidden");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return InvocationHttp.Error(504, "runtime-observation-timeout");
        }
    }

    private static async Task<IResult> RecoverAsync(string workspaceId, string containerId, HttpContext context,
        IPluginInstallationAuthority authority, IVirtualActorProvider actors,
        ICapabilityTokenService tokens, TimeProvider timeProvider, CancellationToken ct)
    {
        const string grant = WorkspaceRuntimeRecovery.RecoverGrant;
        var denial = await authority.DenialAsync(context, workspaceId, grant);
        if (denial is not null)
            return denial;
        if (!InvocationHttp.TryReadCapability(context, tokens, out var token, out var failure))
            return failure;
        if (!InvocationHttp.IsRouteSegment(containerId))
            return InvocationHttp.Error(400, "invalid-container-identity");
        var workspace = actors.GetActor<IWorkspaceActor>(VirtualActorId.From(workspaceId));
        if ((await workspace.GetStateAsync()).WorkspaceId.IsEmpty)
            return InvocationHttp.Error(404, "workspace-not-found");
        var invalidId = ManagementAdmission.ReadId(context, out var managementId);
        if (invalidId is not null)
            return invalidId;
        context.Response.Headers[ManagementAdmission.IdHeader] = managementId;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10), timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        ContainerRecoveryResult result;
        try
        {
            result = await workspace.RecoverContainerAsync(ContainerId.From(containerId), token, managementId, linked.Token);
        }
        catch (UnauthorizedAccessException)
        {
            return InvocationHttp.Error(403, "forbidden");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return InvocationHttp.Error(504, "container-recovery-outcome-unknown");
        }
        return result.Outcome switch
        {
            ContainerRecoveryOutcome.Started or ContainerRecoveryOutcome.AlreadyRunning =>
                Results.Ok(result),
            ContainerRecoveryOutcome.EvidenceUnconfirmed =>
                Results.Json(result, SiloApiJsonContext.Default.ContainerRecoveryResult, statusCode: 500),
            _ => Results.Json(result, SiloApiJsonContext.Default.ContainerRecoveryResult, statusCode: 409)
        };
    }
}
