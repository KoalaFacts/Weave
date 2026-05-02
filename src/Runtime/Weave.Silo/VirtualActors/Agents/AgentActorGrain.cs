using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Agents.Pipeline;
using Weave.Agents.Verification;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Models;

namespace Weave.Silo.VirtualActors;

public sealed class AgentActorGrain : Grain, IAgentActorGrain
{
    private readonly AgentActor _actor;

    public AgentActorGrain(
        IVirtualActorProvider actors,
        IAgentChatPipeline chatPipeline,
        ILifecycleManager lifecycleManager,
        IEventBus eventBus,
        IAgentVerificationDispatcher verificationDispatcher,
        TimeProvider timeProvider,
        ILogger<AgentActor> logger,
        [PersistentState("agent", "Default")] IPersistentState<AgentState> state)
    {
        _actor = new AgentActor(actors, chatPipeline, lifecycleManager, eventBus, verificationDispatcher,
            timeProvider, logger, new OrleansActorState<AgentState>(state));
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        _actor.OnActivatedAsync(this.GetPrimaryKeyString(), cancellationToken);

    public Task<AgentState> ActivateAgentAsync(WorkspaceId workspaceId, AgentDefinition definition) =>
        _actor.ActivateAgentAsync(workspaceId, definition);

    public Task DeactivateAsync() => _actor.DeactivateAsync();
    public Task<AgentState> GetStateAsync() => _actor.GetStateAsync();
    public Task<AgentChatResponse> SendAsync(AgentMessage message) => _actor.SendAsync(message);
    public Task<AgentTaskInfo> SubmitTaskAsync(string description) => _actor.SubmitTaskAsync(description);

    public Task CompleteTaskAsync(AgentTaskId taskId, bool success, ProofOfWork proof) =>
        _actor.CompleteTaskAsync(taskId, success, proof);

    public Task ReviewTaskAsync(AgentTaskId taskId, bool accepted, string? feedback = null, VerificationRecord? verification = null) =>
        _actor.ReviewTaskAsync(taskId, accepted, feedback, verification);

    public Task ConnectToolAsync(string toolName) => _actor.ConnectToolAsync(toolName);
    public Task DisconnectToolAsync(string toolName) => _actor.DisconnectToolAsync(toolName);
}
