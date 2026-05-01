using Weave.Agents.Models;
using Weave.Shared.Ids;

namespace Weave.Agents.Actors;

internal sealed class AgentIdentity
{
    public void Ensure(AgentState state, string? key, WorkspaceId workspaceId)
    {
        if (!string.IsNullOrWhiteSpace(state.AgentId))
        {
            if (state.WorkspaceId.IsEmpty)
                state.WorkspaceId = workspaceId;

            if (string.IsNullOrWhiteSpace(state.AgentName))
                state.AgentName = GetAgentName(state.AgentId);

            return;
        }

        Apply(state, key, workspaceId);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from AgentActor.")]
    public void Apply(AgentState state, string? key, WorkspaceId workspaceId)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            var parts = key.Split('/', 2);
            state.AgentId = key;
            state.WorkspaceId = WorkspaceId.From(parts.Length > 1 ? parts[0] : key);
            state.AgentName = parts.Length > 1 ? parts[1] : key;
            return;
        }

        state.WorkspaceId = workspaceId;
        state.AgentName = string.IsNullOrWhiteSpace(state.AgentName)
            ? "agent"
            : state.AgentName;
        state.AgentId = $"{workspaceId}/{state.AgentName}";
    }

    private static string GetAgentName(string agentId)
    {
        var separatorIndex = agentId.IndexOf('/', StringComparison.Ordinal);
        return separatorIndex >= 0 && separatorIndex < agentId.Length - 1
            ? agentId[(separatorIndex + 1)..]
            : agentId;
    }
}
