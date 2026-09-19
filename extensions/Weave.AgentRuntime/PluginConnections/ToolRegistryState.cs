using Weave.Authority;
using Weave.Workspaces.Manifest;

namespace Weave.Agents.ToolRegistry;

public sealed record ToolRegistryState
{
    public Dictionary<string, ToolDefinition> Definitions { get; init; } = [];
    public Dictionary<string, ToolConnection> Connections { get; init; } = [];
    public Dictionary<string, List<string>> AgentToolAccess { get; init; } = [];
    public string WorkspaceId { get; set; } = string.Empty;

    // Old persisted availability records have no capabilities and fail closed.
    public Dictionary<string, List<string>> AgentCapabilities { get; init; } = [];

    public bool IsToolAllowed(string agentName, string toolName) =>
        AgentToolAccess.TryGetValue(agentName, out var allowed) &&
        allowed.Contains(toolName, StringComparer.Ordinal);

    public IReadOnlyList<string> InvocationGrants(string agentName, string toolName) =>
        IsToolAllowed(agentName, toolName) && AgentCapabilities.TryGetValue(agentName, out var capabilities)
            ? ToolCapability.ConstrainInvocations(capabilities, toolName)
            : [];

    public void GrantTools(string agentName, IReadOnlyList<string> toolNames, IReadOnlyList<string> capabilities)
    {
        // Capture both before mutating state; the caller never owns persisted lists.
        var tools = toolNames.Distinct(StringComparer.Ordinal).ToList();
        var grants = capabilities.Distinct(StringComparer.Ordinal).ToList();
        AgentToolAccess[agentName] = tools;
        AgentCapabilities[agentName] = grants;
    }

    public void ConfigureAccess(IReadOnlyDictionary<string, List<string>> agentToolAccess,
        IReadOnlyDictionary<string, List<string>> agentCapabilities)
    {
        var tools = agentToolAccess.ToDictionary(kvp => kvp.Key,
            kvp => kvp.Value.Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var grants = agentCapabilities.ToDictionary(kvp => kvp.Key,
            kvp => kvp.Value.Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        AgentToolAccess.Clear();
        AgentCapabilities.Clear();
        foreach (var (name, available) in tools)
            AgentToolAccess[name] = available;
        foreach (var (name, capabilities) in grants)
            AgentCapabilities[name] = capabilities;
    }
}
