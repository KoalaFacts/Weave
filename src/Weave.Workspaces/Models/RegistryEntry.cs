using Orleans;

namespace Weave.Workspaces.Models;

[GenerateSerializer]
public sealed record RegistryEntry
{
    [Id(0)] public required string Name { get; init; }
    [Id(1)] public required string Path { get; init; }
    [Id(2)] public WorkspaceStatus Status { get; init; } = WorkspaceStatus.Stopped;
    [Id(3)] public DateTimeOffset? LastActive { get; init; }
}
