namespace Weave.Workspaces.Manifest;

public sealed record WorkspaceHooks
{
    public IReadOnlyList<string> PreStart { get; init; } = [];
    public IReadOnlyList<string> PostStart { get; init; } = [];
    public IReadOnlyList<string> PreStop { get; init; } = [];
    public IReadOnlyList<string> PostStop { get; init; } = [];
}
