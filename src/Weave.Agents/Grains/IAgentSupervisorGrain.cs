using Orleans;
using Weave.Agents.Models;
using Weave.Workspaces.Models;

namespace Weave.Agents.Grains;

public interface IAgentSupervisorGrain : IGrainWithStringKey
{
    Task ActivateAllAsync(WorkspaceManifest manifest);
    Task DeactivateAllAsync();
    Task<IReadOnlyList<AgentState>> GetAllAgentStatesAsync();
    Task<AgentState?> GetAgentStateAsync(string agentName);
}
