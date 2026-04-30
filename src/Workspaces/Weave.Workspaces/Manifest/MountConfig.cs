namespace Weave.Workspaces.Models;

public sealed record MountConfig
{
    public required string Source { get; init; }
    public required string Target { get; init; }
    public bool Readonly { get; init; }
}