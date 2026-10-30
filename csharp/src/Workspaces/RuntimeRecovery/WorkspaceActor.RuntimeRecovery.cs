using Weave.Security.Tokens;
using Weave.Shared.Ids;
using Weave.Workspaces.Runtime;
using Weave.Workspaces.RuntimeRecovery;

namespace Weave.Workspaces.Lifecycle;

public sealed partial class WorkspaceActor
{
    public Task<WorkspaceRuntimeReconciliationResult> ReconcileRuntimeAsync(WorkspaceRuntimeReconciliationRequest request,
        CapabilityToken token, string managementId, CancellationToken ct) =>
        recovery.ReconcileAsync(persistentState.State, request, token, managementId, persistentState.WriteStateAsync, ct);
    public Task<WorkspaceRuntimeSnapshot> ObserveRuntimeAsync(CapabilityToken token, CancellationToken ct) =>
        recovery.ObserveAsync(persistentState.State, token, ct);

    public Task<ContainerRecoveryResult> RecoverContainerAsync(ContainerId containerId, CapabilityToken token,
        string managementId, CancellationToken ct) =>
        recovery.RecoverAsync(persistentState.State, containerId, token, managementId, ct);
}
