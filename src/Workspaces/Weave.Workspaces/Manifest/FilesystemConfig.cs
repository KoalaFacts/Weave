namespace Weave.Workspaces.Models;

public sealed record FilesystemConfig
{
    public string? Root { get; init; }
    public IReadOnlyList<MountConfig> Mounts { get; init; } = [];
}
