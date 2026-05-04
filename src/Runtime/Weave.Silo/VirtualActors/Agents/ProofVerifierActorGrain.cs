using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Models;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Silo.VirtualActors;

public sealed class ProofVerifierActorGrain : Grain, IProofVerifierActorGrain
{
    private readonly ProofVerifierActor _actor;

    public ProofVerifierActorGrain(
        IVirtualActorProvider actors,
        IEventBus eventBus,
        ILogger<ProofVerifierActor> logger,
        [PersistentState("verifier", "Default")] IPersistentState<VerifierState> state)
    {
        _actor = new ProofVerifierActor(actors, eventBus, logger,
            new OrleansActorState<VerifierState>(state));
    }

    public Task VerifyAsync(WorkspaceId workspaceId, string agentName, AgentTaskId taskId, ProofOfWork proof) =>
        _actor.VerifyAsync(workspaceId, agentName, taskId, proof);

    public Task ConfigureAsync(List<VerificationCondition> conditions, int requiredValidators, List<ValidatorConfig>? validatorConfigs = null) =>
        _actor.ConfigureAsync(conditions, requiredValidators, validatorConfigs);

    public Task<List<VerificationCondition>> GetConditionsAsync() => _actor.GetConditionsAsync();
}
