using System.Text.Json.Serialization;

namespace Weave.Workspaces.RuntimeRecovery;

[JsonConverter(typeof(JsonStringEnumConverter<WorkspaceRuntimeReadinessReason>))]
public enum WorkspaceRuntimeReadinessReason
{
    WorkspaceNotRunning,
    RequiresReconciliation,
    NotStartedOnCurrentHost,
    RecoveryConditionUnconfirmed,
    NetworkNotReady,
    NetworkObservationIncomplete,
    ContainerNotRunning,
    ContainerObservationIncomplete,
    ContainerNetworkNotAttached,
    ContainerNetworkObservationIncomplete
}
