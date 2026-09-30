using Weave.Workspaces.Runtime;

namespace Weave.Workspaces.RuntimeRecovery;

public sealed record WorkspaceContainerObservation
{
    public required string ContainerId { get; init; }
    public required string Name { get; init; }
    public required string RegisteredStatus { get; init; }
    public ContainerRuntimeCondition Condition { get; init; }
}
