namespace Weave.Workspaces.Runtime;

public sealed record ContainerRecoveryResult
{
    public required string ContainerId { get; init; }
    public ContainerRecoveryOutcome Outcome { get; init; }
    public ContainerRuntimeCondition Condition { get; init; }
    public bool Dispatched { get; init; }
    public NetworkRuntimeObservation Network { get; init; } = new();
    public ContainerNetworkCondition NetworkAttachment { get; init; }
}
