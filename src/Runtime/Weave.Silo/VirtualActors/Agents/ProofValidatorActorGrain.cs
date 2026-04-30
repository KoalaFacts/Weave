using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Agents.Pipeline;

namespace Weave.Silo.VirtualActors;

public sealed class ProofValidatorActorGrain : Grain, IProofValidatorActorGrain
{
    private readonly ProofValidatorActor _actor;

    public ProofValidatorActorGrain(
        IAgentChatClientFactory chatClientFactory,
        ILogger<ProofValidatorActor> logger)
    {
        _actor = new ProofValidatorActor(chatClientFactory, logger);
    }

    public Task<VerificationVote> ValidateAsync(string validatorId, ProofOfWork proof, List<VerificationCondition> conditions, string? modelId = null) =>
        _actor.ValidateAsync(validatorId, proof, conditions, modelId);
}
