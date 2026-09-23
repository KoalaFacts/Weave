namespace Weave.Workspaces.Runtime;

public sealed record NetworkSpec
{
    public required string Name { get; init; }
    public string? Subnet { get; init; }
}
