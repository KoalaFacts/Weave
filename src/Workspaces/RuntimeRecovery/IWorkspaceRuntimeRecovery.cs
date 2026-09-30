using Weave.Security.Tokens;
using Weave.Shared.Ids;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Runtime;

namespace Weave.Workspaces.RuntimeRecovery;

public interface IWorkspaceRuntimeRecovery
{
    Task<WorkspaceRuntimeReconciliationResult> ReconcileAsync(WorkspaceState state,
        WorkspaceRuntimeReconciliationRequest request, CapabilityToken token, string managementId,
        Func<CancellationToken, Task> persist, CancellationToken ct);
    Task<WorkspaceRuntimeSnapshot> ObserveAsync(WorkspaceState state, CapabilityToken token, CancellationToken ct);
    Task<ContainerRecoveryResult> RecoverAsync(WorkspaceState state, ContainerId containerId,
        CapabilityToken token, string managementId, CancellationToken ct);
}
