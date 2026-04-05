using Microsoft.Extensions.Logging;
using Weave.Agents.Events;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

public sealed class ProofVerifierGrainTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");
    private static readonly AgentTaskId TestTaskId = AgentTaskId.From("task-1");

    private static IPersistentState<VerifierState> CreatePersistentState(VerifierState? state = null)
    {
        state ??= new VerifierState();
        var persistentState = Substitute.For<IPersistentState<VerifierState>>();
        persistentState.State.Returns(state);
        persistentState.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync().Returns(Task.CompletedTask);
        return persistentState;
    }

    private static (ProofVerifierGrain Grain, IEventBus EventBus, IAgentGrain AgentGrain, IGrainFactory GrainFactory) CreateVerifier(
        VerifierState? state = null,
        Func<string, ProofOfWork, List<VerificationCondition>, string?, VerificationVote>? validatorBehavior = null)
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var eventBus = Substitute.For<IEventBus>();
        var logger = Substitute.For<ILogger<ProofVerifierGrain>>();
        var agentGrain = Substitute.For<IAgentGrain>();
        var persistentState = CreatePersistentState(state);

        grainFactory.GetGrain<IAgentGrain>($"{TestWorkspaceId}/researcher", null)
            .Returns(agentGrain);

        // Set up validator grains
        for (var i = 0; i < 10; i++)
        {
            var validatorId = $"validator-{i}";
            var validatorGrain = Substitute.For<IProofValidatorGrain>();
            var capturedId = validatorId;
            validatorGrain.ValidateAsync(capturedId, Arg.Any<ProofOfWork>(), Arg.Any<List<VerificationCondition>>(), Arg.Any<string?>())
                .Returns(callInfo =>
                {
                    if (validatorBehavior is not null)
                        return validatorBehavior(capturedId, callInfo.Arg<ProofOfWork>(), callInfo.Arg<List<VerificationCondition>>(), callInfo.ArgAt<string?>(3));

                    // Default: accept everything
                    return new VerificationVote
                    {
                        ValidatorId = capturedId,
                        Accepted = true,
                        Reason = "All conditions satisfied."
                    };
                });
            grainFactory.GetGrain<IProofValidatorGrain>($"{TestWorkspaceId}/{validatorId}", null)
                .Returns(validatorGrain);
        }

        var grain = new ProofVerifierGrain(grainFactory, eventBus, logger, persistentState);
        return (grain, eventBus, agentGrain, grainFactory);
    }

    [Fact]
    public async Task VerifyAsync_AllValidatorsAccept_AcceptsTask()
    {
        var (verifier, _, agentGrain, _) = CreateVerifier();
        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "passed" }]
        };

        await verifier.VerifyAsync(TestWorkspaceId, "researcher", TestTaskId, proof);

#pragma warning disable xUnit1051
        await agentGrain.Received(1).ReviewTaskAsync(
            TestTaskId,
            true,
            Arg.Is<string>(s => s.Contains("2/2")),
            Arg.Is<VerificationRecord>(v => v.Accepted && v.Votes.Count == 2));
#pragma warning restore xUnit1051
    }

    [Fact]
    public async Task VerifyAsync_MajorityRejects_RejectsTask()
    {
        var callCount = 0;
        var (verifier, _, agentGrain, _) = CreateVerifier(validatorBehavior: (id, _, _, _) =>
        {
            var reject = Interlocked.Increment(ref callCount) <= 2;
            return new VerificationVote
            {
                ValidatorId = id,
                Accepted = !reject,
                Reason = reject ? "CI failing" : "All conditions satisfied."
            };
        });

        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "red" }]
        };

        await verifier.VerifyAsync(TestWorkspaceId, "researcher", TestTaskId, proof);

#pragma warning disable xUnit1051
        await agentGrain.Received(1).ReviewTaskAsync(
            TestTaskId,
            false,
            Arg.Is<string>(s => s.Contains("0/2")),
            Arg.Is<VerificationRecord>(v => !v.Accepted));
#pragma warning restore xUnit1051
    }

    [Fact]
    public async Task VerifyAsync_MajorityAccepts_WithThreeValidators_AcceptsTask()
    {
        var callCount = 0;
        var state = new VerifierState { RequiredValidators = 3 };
        var (verifier, _, agentGrain, _) = CreateVerifier(state, validatorBehavior: (id, _, _, _) =>
        {
            var accept = Interlocked.Increment(ref callCount) <= 2;
            return new VerificationVote
            {
                ValidatorId = id,
                Accepted = accept,
                Reason = accept ? "All conditions satisfied." : "CI failing"
            };
        });

        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "passed" }]
        };

        await verifier.VerifyAsync(TestWorkspaceId, "researcher", TestTaskId, proof);

#pragma warning disable xUnit1051
        await agentGrain.Received(1).ReviewTaskAsync(
            TestTaskId,
            true,
            Arg.Is<string>(s => s.Contains("2/3")),
            Arg.Is<VerificationRecord>(v => v.Accepted && v.ConsensusReached));
#pragma warning restore xUnit1051
    }

    [Fact]
    public async Task VerifyAsync_PublishesEventWithVoteCounts()
    {
        var (verifier, eventBus, _, _) = CreateVerifier();
        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "success" }]
        };

        await verifier.VerifyAsync(TestWorkspaceId, "researcher", TestTaskId, proof);

        await eventBus.Received(1).PublishAsync(
            Arg.Is<ProofVerifiedEvent>(e =>
                e.WorkspaceId == TestWorkspaceId &&
                e.AgentName == "researcher" &&
                e.TaskId == TestTaskId &&
                e.Accepted &&
                e.VoteCount == 2 &&
                e.AcceptCount == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyAsync_VerificationRecordIncludesAllVotes()
    {
        var (verifier, _, agentGrain, _) = CreateVerifier();
        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.Custom, Label = "Note", Value = "done" }]
        };

        await verifier.VerifyAsync(TestWorkspaceId, "researcher", TestTaskId, proof);

#pragma warning disable xUnit1051
        await agentGrain.Received(1).ReviewTaskAsync(
            TestTaskId,
            true,
            Arg.Any<string>(),
            Arg.Is<VerificationRecord>(v =>
                v.Votes.Count == 2 &&
                v.RequiredVotes == 2 &&
                v.ConsensusReached));
#pragma warning restore xUnit1051
    }

    [Fact]
    public async Task ConfigureAsync_StoresConditionsValidatorCountAndModels()
    {
        var state = new VerifierState();
        var (verifier, _, _, _) = CreateVerifier(state);
        var conditions = new List<VerificationCondition>
        {
            new() { Name = "test-rule", Description = "All tests must pass." }
        };
        var configs = new List<ValidatorConfig>
        {
            new() { ModelId = "gpt-4o" },
            new() { ModelId = "claude-sonnet-4-20250514" }
        };

        await verifier.ConfigureAsync(conditions, 2, configs);

        state.Conditions.Count.ShouldBe(1);
        state.Conditions[0].Name.ShouldBe("test-rule");
        state.RequiredValidators.ShouldBe(2);
        state.ValidatorConfigs.Count.ShouldBe(2);
        state.ValidatorConfigs[0].ModelId.ShouldBe("gpt-4o");
        state.ValidatorConfigs[1].ModelId.ShouldBe("claude-sonnet-4-20250514");
    }

    [Fact]
    public async Task GetConditionsAsync_ReturnsDefaultsWhenNotConfigured()
    {
        var (verifier, _, _, _) = CreateVerifier();

        var conditions = await verifier.GetConditionsAsync();

        conditions.Count.ShouldBeGreaterThan(0);
        conditions.ShouldContain(c => c.Name == "ci-passing");
        conditions.ShouldContain(c => c.Name == "tests-passing");
        conditions.ShouldContain(c => c.Name == "pr-has-link");
    }

    [Fact]
    public async Task GetConditionsAsync_DefaultConditionsArePlainLanguage()
    {
        var (verifier, _, _, _) = CreateVerifier();

        var conditions = await verifier.GetConditionsAsync();

        foreach (var condition in conditions)
        {
            condition.Name.ShouldNotBeNullOrWhiteSpace();
            condition.Description.ShouldNotBeNullOrWhiteSpace();
            condition.Description.Length.ShouldBeGreaterThan(10);
        }
    }

    [Fact]
    public async Task GetConditionsAsync_ReturnsConfiguredConditions()
    {
        var state = new VerifierState
        {
            Conditions = [new() { Name = "custom-rule", Description = "Custom proof must be complete and thorough." }]
        };
        var (verifier, _, _, _) = CreateVerifier(state);

        var conditions = await verifier.GetConditionsAsync();

        conditions.Count.ShouldBe(1);
        conditions[0].Name.ShouldBe("custom-rule");
    }

    [Fact]
    public async Task VerifyAsync_UsesConfiguredValidatorCount()
    {
        var state = new VerifierState { RequiredValidators = 5 };
        var (verifier, _, agentGrain, _) = CreateVerifier(state);
        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.Custom, Label = "Note", Value = "done" }]
        };

        await verifier.VerifyAsync(TestWorkspaceId, "researcher", TestTaskId, proof);

#pragma warning disable xUnit1051
        await agentGrain.Received(1).ReviewTaskAsync(
            TestTaskId,
            true,
            Arg.Is<string>(s => s.Contains("5/5")),
            Arg.Is<VerificationRecord>(v => v.Votes.Count == 5));
#pragma warning restore xUnit1051
    }

    [Fact]
    public async Task VerifyAsync_PassesModelIdToValidators()
    {
        var modelsUsed = new List<string?>();
        var state = new VerifierState
        {
            RequiredValidators = 2,
            ValidatorConfigs =
            [
                new() { ModelId = "gpt-4o" },
                new() { ModelId = "claude-sonnet-4-20250514" }
            ]
        };
        var (verifier, _, _, _) = CreateVerifier(state, validatorBehavior: (id, _, _, modelId) =>
        {
            modelsUsed.Add(modelId);
            return new VerificationVote
            {
                ValidatorId = id,
                Accepted = true,
                Reason = "All conditions satisfied."
            };
        });

        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "passed" }]
        };

        await verifier.VerifyAsync(TestWorkspaceId, "researcher", TestTaskId, proof);

        modelsUsed.Count.ShouldBe(2);
        modelsUsed.ShouldContain("gpt-4o");
        modelsUsed.ShouldContain("claude-sonnet-4-20250514");
    }

    [Fact]
    public async Task VerifyAsync_ValidatorConfigsShorterThanCount_RemainingUseNull()
    {
        var modelsUsed = new List<string?>();
        var state = new VerifierState
        {
            RequiredValidators = 3,
            ValidatorConfigs = [new() { ModelId = "gpt-4o" }]
        };
        var (verifier, _, _, _) = CreateVerifier(state, validatorBehavior: (id, _, _, modelId) =>
        {
            modelsUsed.Add(modelId);
            return new VerificationVote
            {
                ValidatorId = id,
                Accepted = true,
                Reason = "All conditions satisfied."
            };
        });

        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "passed" }]
        };

        await verifier.VerifyAsync(TestWorkspaceId, "researcher", TestTaskId, proof);

        modelsUsed.Count.ShouldBe(3);
        modelsUsed[0].ShouldBe("gpt-4o");
        modelsUsed[1].ShouldBeNull();
        modelsUsed[2].ShouldBeNull();
    }

    [Fact]
    public async Task VerifyAsync_UsesConfiguredConditions()
    {
        var state = new VerifierState
        {
            RequiredValidators = 1,
            Conditions = [new() { Name = "must-pass", Description = "The build must pass all checks." }]
        };
        var validatorCalled = false;
        var (verifier, _, _, _) = CreateVerifier(state, validatorBehavior: (id, _, conditions, _) =>
        {
            validatorCalled = true;
            conditions.Count.ShouldBe(1);
            conditions[0].Name.ShouldBe("must-pass");
            return new VerificationVote
            {
                ValidatorId = id,
                Accepted = true,
                Reason = "All conditions satisfied."
            };
        });

        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "green" }]
        };

        await verifier.VerifyAsync(TestWorkspaceId, "researcher", TestTaskId, proof);

        validatorCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task VerifyAsync_DefaultsToTwoValidators()
    {
        var (verifier, eventBus, _, _) = CreateVerifier();
        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "passed" }]
        };

        await verifier.VerifyAsync(TestWorkspaceId, "researcher", TestTaskId, proof);

        await eventBus.Received(1).PublishAsync(
            Arg.Is<ProofVerifiedEvent>(e => e.VoteCount == 2),
            Arg.Any<CancellationToken>());
    }
}
