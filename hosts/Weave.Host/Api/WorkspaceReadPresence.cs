using Weave.Workspaces.Lifecycle;

namespace Weave.Silo.Api;

internal static class WorkspaceReadPresence
{
    public static bool Exists(WorkspaceState state) => !state.WorkspaceId.IsEmpty
        // Older read-only activations persisted only the actor key. Preserve every
        // lifecycle or resource signal; recognizing that shape must not rewrite it.
        && (state.Status != WorkspaceStatus.Stopped
            || state.RecoveryCondition != WorkspaceRecoveryCondition.NotApplicable
            || state.RuntimeInstanceId != Guid.Empty
            || state.RuntimeName is not null
            || state.Name is not null
            || state.StartedAt is not null
            || state.StoppedAt is not null
            || state.ErrorMessage is not null
            || state.NetworkId is not null
            || state.Containers.Count != 0
            || state.ActiveAgents.Count != 0
            || state.ActiveTools.Count != 0
            || state.ActivePlugins.Count != 0
            || state.DaprToolInstallations.Count != 0
            || state.McpToolInstallations.Count != 0);
}
