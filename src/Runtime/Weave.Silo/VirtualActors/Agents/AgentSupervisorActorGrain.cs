using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Models;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
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
