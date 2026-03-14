using Microsoft.Extensions.Logging;
using Weave.Agents.Events;
using Weave.Agents.Models;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Grains;

public sealed class ProofVerifierGrain(
    IGrainFactory grainFactory,
    IEventBus eventBus,
    ILogger<ProofVerifierGrain> logger,
    [PersistentState("verifier", "Default")] IPersistentState<VerifierState> persistentState) : Grain, IProofVerifierGrain
{
    internal static readonly List<VerificationCondition> DefaultConditions =
    [
        new() { Name = "ci-passing", Description = "The CI/build status must indicate that the build passed successfully. Look for indicators like 'passed', 'success', or 'green'." },
        new() { Name = "tests-passing", Description = "Test results must not indicate any failures. The test output should show all tests passing with no errors or failures." },
        new() { Name = "pr-has-link", Description = "If a pull request is referenced, it must include a URI link to the actual pull request." },
        new() { Name = "code-review-present", Description = "Code review evidence must be present with a meaningful, non-empty value describing the review outcome." },
        new() { Name = "diff-present", Description = "A diff summary must be present showing what code changes were made." },
        new() { Name = "custom-present", Description = "Any custom proof items must have meaningful, non-empty values." }
    ];

    public async Task ConfigureAsync(List<VerificationCondition> conditions, int requiredValidators, List<ValidatorConfig>? validatorConfigs = null)
    {
        persistentState.State.Conditions.Clear();
        persistentState.State.Conditions.AddRange(conditions);
        persistentState.State.RequiredValidators = Math.Max(1, requiredValidators);
        persistentState.State.ValidatorConfigs.Clear();
        if (validatorConfigs is not null)
            persistentState.State.ValidatorConfigs.AddRange(validatorConfigs);
        await persistentState.WriteStateAsync();
    }

    public Task<List<VerificationCondition>> GetConditionsAsync()
    {
        var conditions = persistentState.State.Conditions.Count > 0
            ? persistentState.State.Conditions
            : DefaultConditions;
        return Task.FromResult(conditions.ToList());
    }

    public async Task VerifyAsync(WorkspaceId workspaceId, string agentName, AgentTaskId taskId, ProofOfWork proof)
    {
        var conditions = persistentState.State.Conditions.Count > 0
            ? persistentState.State.Conditions
            : DefaultConditions;

        var validatorCount = persistentState.State.RequiredValidators > 0
            ? persistentState.State.RequiredValidators
            : 2;

        var validatorConfigs = persistentState.State.ValidatorConfigs;

        // Dispatch to N independent validator grains in parallel — each may use a different model
        var validationTasks = new List<Task<VerificationVote>>(validatorCount);
        for (var i = 0; i < validatorCount; i++)
        {
            var validatorId = $"validator-{i}";
            var modelId = i < validatorConfigs.Count ? validatorConfigs[i].ModelId : null;
            var validatorGrain = grainFactory.GetGrain<IProofValidatorGrain>($"{workspaceId}/{validatorId}");
            validationTasks.Add(validatorGrain.ValidateAsync(validatorId, proof, conditions, modelId));
        }

        var votes = await Task.WhenAll(validationTasks);

        // Determine consensus — majority must accept
        var acceptCount = 0;
        foreach (var vote in votes)
        {
            if (vote.Accepted)
                acceptCount++;
        }

        var majorityThreshold = (validatorCount / 2) + 1;
        var consensusReached = acceptCount >= majorityThreshold || (validatorCount - acceptCount) >= majorityThreshold;
        var accepted = acceptCount >= majorityThreshold;

        var verification = new VerificationRecord
        {
            Votes = [.. votes],
            RequiredVotes = majorityThreshold,
            ConsensusReached = consensusReached,
            Accepted = accepted
        };

        var feedback = accepted
            ? $"Consensus reached: {acceptCount}/{validatorCount} validators accepted."
            : BuildRejectionFeedback(votes, acceptCount, validatorCount);

        logger.LogInformation(
            "Verification for task {TaskId} on agent {AgentName}: consensus={Accepted}, votes={AcceptCount}/{Total}, threshold={Threshold}",
            taskId,
            agentName,
            accepted,
            acceptCount,
            validatorCount,
            majorityThreshold);

        var agentGrain = grainFactory.GetGrain<IAgentGrain>($"{workspaceId}/{agentName}");

        try
        {
            await agentGrain.ReviewTaskAsync(taskId, accepted, feedback, verification);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to deliver review for task {TaskId} on agent {AgentName}", taskId, agentName);
        }

        await eventBus.PublishAsync(new ProofVerifiedEvent
        {
            SourceId = $"{workspaceId}/verifier",
            WorkspaceId = workspaceId,
            AgentName = agentName,
            TaskId = taskId,
            Accepted = accepted,
            Feedback = feedback,
            VoteCount = validatorCount,
            AcceptCount = acceptCount
        }, CancellationToken.None);
    }

    private static string BuildRejectionFeedback(VerificationVote[] votes, int acceptCount, int validatorCount)
    {
        var rejections = new List<string>();
        foreach (var vote in votes)
        {
            if (!vote.Accepted)
                rejections.Add($"{vote.ValidatorId}: {vote.Reason}");
        }

        return $"Consensus not reached: {acceptCount}/{validatorCount} accepted. Rejections: {string.Join("; ", rejections)}";
    }
}
