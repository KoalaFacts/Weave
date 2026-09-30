using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Runtime;

namespace Weave.Workspaces.RuntimeRecovery;

public sealed record WorkspaceRuntimeReadiness
{
    public WorkspaceRuntimeReadinessCondition Condition { get; init; }
    public IReadOnlyList<WorkspaceRuntimeReadinessReason> Reasons { get; init; } = [];

    internal static WorkspaceRuntimeReadiness Evaluate(WorkspaceState state, bool confirmedOnCurrentHost,
        NetworkRuntimeObservation network, IReadOnlyList<WorkspaceContainerObservation> containers)
    {
        var reasons = new List<WorkspaceRuntimeReadinessReason>();
        var blocked = false;
        if (state.Status is not WorkspaceStatus.Running)
            Add(WorkspaceRuntimeReadinessReason.WorkspaceNotRunning, true);
        if (state.RecoveryCondition is WorkspaceRecoveryCondition.RequiresReconciliation)
            Add(WorkspaceRuntimeReadinessReason.RequiresReconciliation, true);
        else if (state.RecoveryCondition is not (WorkspaceRecoveryCondition.StartedOnThisHost
            or WorkspaceRecoveryCondition.RuntimeReconciledOnThisHost))
            Add(WorkspaceRuntimeReadinessReason.RecoveryConditionUnconfirmed, false);
        if (!confirmedOnCurrentHost)
            Add(state.RecoveryCondition is WorkspaceRecoveryCondition.RuntimeReconciledOnThisHost
                ? WorkspaceRuntimeReadinessReason.NotReconciledOnCurrentHost
                : WorkspaceRuntimeReadinessReason.NotStartedOnCurrentHost, true);

        switch (network.Condition)
        {
            case NetworkRuntimeCondition.Present:
                break;
            case NetworkRuntimeCondition.NotRequired when containers.Count == 0:
                break;
            case NetworkRuntimeCondition.Missing or NetworkRuntimeCondition.InvalidIdentity
                or NetworkRuntimeCondition.RuntimeMismatch or NetworkRuntimeCondition.NotRecorded:
                Add(WorkspaceRuntimeReadinessReason.NetworkNotReady, true);
                break;
            default:
                Add(WorkspaceRuntimeReadinessReason.NetworkObservationIncomplete, false);
                break;
        }
        foreach (var container in containers)
        {
            switch (container.Condition)
            {
                case ContainerRuntimeCondition.Running:
                    break;
                case ContainerRuntimeCondition.Stopped or ContainerRuntimeCondition.Missing
                    or ContainerRuntimeCondition.Transitioning or ContainerRuntimeCondition.InvalidIdentity
                    or ContainerRuntimeCondition.RuntimeMismatch or ContainerRuntimeCondition.WorkspaceNotRunning:
                    Add(WorkspaceRuntimeReadinessReason.ContainerNotRunning, true);
                    break;
                default:
                    Add(WorkspaceRuntimeReadinessReason.ContainerObservationIncomplete, false);
                    break;
            }
            switch (container.NetworkAttachment)
            {
                case ContainerNetworkCondition.Attached:
                    break;
                case ContainerNetworkCondition.Detached or ContainerNetworkCondition.InvalidIdentity:
                    Add(WorkspaceRuntimeReadinessReason.ContainerNetworkNotAttached, true);
                    break;
                default:
                    Add(WorkspaceRuntimeReadinessReason.ContainerNetworkObservationIncomplete, false);
                    break;
            }
        }
        return new WorkspaceRuntimeReadiness
        {
            Condition = blocked ? WorkspaceRuntimeReadinessCondition.NotReady
                : reasons.Count > 0 ? WorkspaceRuntimeReadinessCondition.Unknown
                : WorkspaceRuntimeReadinessCondition.Ready,
            Reasons = reasons.AsReadOnly()
        };

        void Add(WorkspaceRuntimeReadinessReason reason, bool preventsReadiness)
        {
            blocked |= preventsReadiness;
            if (!reasons.Contains(reason))
                reasons.Add(reason);
        }
    }
}
