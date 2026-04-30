namespace Weave.Workspaces.Models;

public sealed record ScalingConfig
{
    public int Min { get; init; } = 1;
    public int Max { get; init; } = 1;
}
