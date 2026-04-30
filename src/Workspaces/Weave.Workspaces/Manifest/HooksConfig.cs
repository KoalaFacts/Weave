namespace Weave.Workspaces.Models;

public sealed record HooksConfig
{
    public WorkspaceHooks? Workspace { get; init; }
    public Dictionary<string, AgentHooks>? Agents { get; init; }
    public Dictionary<string, ToolHooks>? Tools { get; init; }
}