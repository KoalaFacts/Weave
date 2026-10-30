namespace Weave.Workspaces.Manifest;

public sealed record TargetDefinition
{
    public required string Runtime { get; init; }
    public int Replicas { get; init; } = 1;
    public string? Trigger { get; init; }
    public string? Region { get; init; }
    public ScalingConfig? Scaling { get; init; }
}
