namespace Weave.Workspaces.Models;

public sealed record WorkspaceHooks
{
    public List<string> PreStart { get; init; } = [];
    public List<string> PostStart { get; init; } = [];
    public List<string> PreStop { get; init; } = [];
    public List<string> PostStop { get; init; } = [];
}