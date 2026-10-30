using Weave.Security.Tokens;
using Weave.Workspaces.Manifest;

namespace Weave.Agents.ToolRegistry;

public sealed record ToolRegistryState
{
    public Dictionary<string, ToolDefinition> Definitions { get; init; } = [];
    public Dictionary<string, ToolConnection> Connections { get; init; } = [];
    public Dictionary<string, List<string>> AgentToolAccess { get; init; } = [];
    // Missing in previously persisted state: availability alone must grant nothing.
    public Dictionary<string, List<string>> AgentCapabilities { get; init; } = [];
    public string WorkspaceId { get; set; } = string.Empty;

    public bool IsToolAllowed(string agentName, string toolName) =>
        AgentToolAccess.TryGetValue(agentName, out var allowed) &&
        allowed.Contains(toolName, StringComparer.Ordinal);

    public HashSet<string> GetInvocationGrants(string agentName, string toolName) =>
        IsToolAllowed(agentName, toolName) && AgentCapabilities.TryGetValue(agentName, out var capabilities)
            ? ToolCapability.ConstrainInvocations(toolName, capabilities)
            : [];

    public void GrantTools(string agentName, IReadOnlyList<string> toolNames, IReadOnlyList<string> capabilities)
    {
        List<string> tools = [.. toolNames.Distinct(StringComparer.Ordinal)];
        List<string> grants = [.. capabilities.Distinct(StringComparer.Ordinal)];
        AgentToolAccess[agentName] = tools;
        AgentCapabilities[agentName] = grants;
    }

    public void ConfigureAccess(
        IReadOnlyDictionary<string, List<string>> agentToolAccess,
        IReadOnlyDictionary<string, List<string>> agentCapabilities)
    {
        // Finish both snapshots before replacing state; inputs may alias these maps.
        var tools = agentToolAccess.ToDictionary(pair => pair.Key,
            pair => pair.Value.Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var grants = tools.Keys.ToDictionary(agentName => agentName,
            agentName => (agentCapabilities.GetValueOrDefault(agentName) ?? [])
                .Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        AgentToolAccess.Clear();
        AgentCapabilities.Clear();
        foreach (var (agentName, available) in tools)
        {
            AgentToolAccess[agentName] = available;
            AgentCapabilities[agentName] = grants[agentName];
        }
    }
}
