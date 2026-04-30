namespace Weave.Workspaces.Models;

public sealed record WorkspaceManifest
{
    public required string Version { get; init; }
    public required string Name { get; init; }
    public WorkspaceConfig Workspace { get; init; } = new();
    public Dictionary<string, AgentDefinition> Agents { get; init; } = [];
    public Dictionary<string, ToolDefinition> Tools { get; init; } = [];
    public Dictionary<string, TargetDefinition> Targets { get; init; } = [];
    public HooksConfig? Hooks { get; init; }
    public Dictionary<string, PluginDefinition> Plugins { get; init; } = [];
    public Dictionary<string, ChannelDefinition> Channels { get; init; } = [];
}