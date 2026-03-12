using Weave.Workspaces.Models;

namespace Weave.Agents.Models;

[GenerateSerializer]
public sealed record ToolRegistryState
{
    [Id(0)] public Dictionary<string, ToolDefinition> Definitions { get; init; } = [];
    [Id(1)] public Dictionary<string, ToolConnection> Connections { get; init; } = [];
    [Id(2)] public Dictionary<string, List<string>> AgentToolAccess { get; init; } = [];
    [Id(3)] public string WorkspaceId { get; set; } = string.Empty;
}
