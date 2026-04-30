using Weave.Workspaces.Models;

namespace Weave.Agents.Models;

public sealed record ToolRegistryState
{
    public Dictionary<string, ToolDefinition> Definitions { get; init; } = [];
    public Dictionary<string, ToolConnection> Connections { get; init; } = [];
    public Dictionary<string, List<string>> AgentToolAccess { get; init; } = [];
    public string WorkspaceId { get; set; } = string.Empty;
}