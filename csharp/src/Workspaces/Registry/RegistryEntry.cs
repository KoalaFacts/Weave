using Weave.Workspaces.Lifecycle;

namespace Weave.Workspaces.Registry;

public sealed record RegistryEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public WorkspaceStatus Status { get; init; } = WorkspaceStatus.Stopped;
    public DateTimeOffset? LastActive { get; init; }
}
