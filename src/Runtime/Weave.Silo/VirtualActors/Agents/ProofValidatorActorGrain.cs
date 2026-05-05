using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Verification;
using Weave.Agents.Pipeline;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;

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
