namespace Weave.Workspaces.Models;

[GenerateSerializer]
public sealed record WorkspaceRegistryState
{
    [Id(0)] public List<string> WorkspaceIds { get; init; } = [];
}
