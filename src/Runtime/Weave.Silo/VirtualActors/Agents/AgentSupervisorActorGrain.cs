using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Workspaces.Models;

namespace Weave.Silo.VirtualActors;

public sealed class AgentSupervisorActorGrain : Grain, IAgentSupervisorActorGrain
{
    private readonly AgentSupervisorActor _actor;

    public AgentSupervisorActorGrain(
        IVirtualActorProvider actors,
        ILogger<AgentSupervisorActor> logger,
        [PersistentState("agent-supervisor", "Default")] IPersistentState<AgentSupervisorState> state)
    {
        _actor = new AgentSupervisorActor(actors, logger,
            new OrleansActorState<AgentSupervisorState>(state));
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        _actor.OnActivatedAsync(this.GetPrimaryKeyString(), cancellationToken);

    public Task ActivateAllAsync(WorkspaceManifest manifest) => _actor.ActivateAllAsync(manifest);
    public Task DeactivateAllAsync() => _actor.DeactivateAllAsync();
    public Task<IReadOnlyList<AgentState>> GetAllAgentStatesAsync() => _actor.GetAllAgentStatesAsync();
    public Task<AgentState?> GetAgentStateAsync(string agentName) => _actor.GetAgentStateAsync(agentName);
}
