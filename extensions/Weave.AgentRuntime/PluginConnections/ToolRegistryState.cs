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
        AgentToolAccess[agentName] = [.. toolNames.Distinct(StringComparer.Ordinal)];
        AgentCapabilities[agentName] = [.. capabilities.Distinct(StringComparer.Ordinal)];
    }

    public void ConfigureAccess(
        IReadOnlyDictionary<string, List<string>> agentToolAccess,
        IReadOnlyDictionary<string, List<string>> agentCapabilities)
    {
        AgentToolAccess.Clear();
        AgentCapabilities.Clear();
        foreach (var (agentName, toolNames) in agentToolAccess)
            GrantTools(agentName, toolNames, agentCapabilities.GetValueOrDefault(agentName) ?? []);
    }
}
