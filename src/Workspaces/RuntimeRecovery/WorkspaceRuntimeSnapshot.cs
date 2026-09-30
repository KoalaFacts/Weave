using Weave.Workspaces.Runtime;

namespace Weave.Workspaces.RuntimeRecovery;

public sealed record WorkspaceRuntimeSnapshot
{
    public required string WorkspaceId { get; init; }
    public required string RegisteredStatus { get; init; }
    public required string RecoveryCondition { get; init; }
    public string? CreatingRuntime { get; init; }
    public required string CurrentRuntime { get; init; }
    public bool StartedOnCurrentHost { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public NetworkRuntimeObservation Network { get; init; } = new();
    public IReadOnlyList<WorkspaceContainerObservation> Containers { get; init; } = [];
}
