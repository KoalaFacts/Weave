namespace Weave.Workspaces.Models;

public sealed record FilesystemConfig
{
    public string? Root { get; init; }
    public List<MountConfig> Mounts { get; init; } = [];
}