using Weave.Agents.Models;
using Weave.Workspaces.Models;

namespace Weave.Agents.Actors;

public interface IAgentSupervisorActor : IVirtualActorWithStringKey
{
    Task ActivateAllAsync(WorkspaceManifest manifest);
    Task DeactivateAllAsync();
    Task<IReadOnlyList<AgentState>> GetAllAgentStatesAsync();
    Task<AgentState?> GetAgentStateAsync(string agentName);
}
