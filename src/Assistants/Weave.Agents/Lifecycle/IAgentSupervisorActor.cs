using Weave.Agents.Models;
using Weave.Workspaces.Models;

namespace Weave.Agents.Lifecycle;

public interface IAgentSupervisorActor
{
    Task ActivateAllAsync(WorkspaceManifest manifest);
    Task DeactivateAllAsync();
    Task<IReadOnlyList<AgentState>> GetAllAgentStatesAsync();
    Task<AgentState?> GetAgentStateAsync(string agentName);
}
