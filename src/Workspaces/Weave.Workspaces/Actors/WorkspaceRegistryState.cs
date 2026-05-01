namespace Weave.Workspaces.Models;

public sealed record WorkspaceRegistryState
{
    public List<string> WorkspaceIds { get; init; } = [];
}
