namespace Weave.Workspaces.Runtime;

public sealed record NetworkRuntimeObservation
{
    public string? NetworkId { get; init; }
    public NetworkRuntimeCondition Condition { get; init; }
}
