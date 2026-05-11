using Weave.Workspaces.Manifest;

namespace Weave.Agents.ToolRegistry;

public sealed record ToolRegistryState
{
    public Dictionary<string, ToolDefinition> Definitions { get; init; } = [];
    public Dictionary<string, ToolConnection> Connections { get; init; } = [];
    public Dictionary<string, List<string>> AgentToolAccess { get; init; } = [];
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>
    /// Whether the named agent has been granted access to the named tool.
    /// Two-step lookup (agent → tool list → contains) lives here so the
    /// actor doesn't need to know the access-map's internal shape.
    /// </summary>
    public bool IsToolAllowed(string agentName, string toolName) =>
        AgentToolAccess.TryGetValue(agentName, out var allowed) &&
        allowed.Contains(toolName, StringComparer.Ordinal);

    /// <summary>
    /// Replaces the tool list for one agent. The distinct-list invariant
    /// (no duplicate tool names per agent) is enforced here, not at every
    /// caller.
    /// </summary>
    public void GrantTools(string agentName, IReadOnlyList<string>? toolNames) =>
        AgentToolAccess[agentName] = toolNames is null
            ? []
            : [.. toolNames.Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Replaces the entire access map (clear + bulk re-add with distinct
    /// normalization per agent).
    /// </summary>
    public void ConfigureAccess(IReadOnlyDictionary<string, List<string>> agentToolAccess)
    {
        AgentToolAccess.Clear();
        foreach (var (agentName, toolNames) in agentToolAccess)
            AgentToolAccess[agentName] = [.. toolNames.Distinct(StringComparer.Ordinal)];
    }
}
