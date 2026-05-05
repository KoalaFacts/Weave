namespace Weave.Workspaces.Registry;

public sealed record WorkspaceRegistryState
{
    public List<string> WorkspaceIds { get; init; } = [];
}
