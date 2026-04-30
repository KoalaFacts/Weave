using Microsoft.Extensions.Logging;
using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Agents.Pipeline;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Models;

namespace Weave.Agents.Tests;

public sealed class AgentActorEpisodeWiringTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");
    private const string TestAgentName = "researcher";

    private sealed class Fixture
    {
        public IActorState<AgentState> PersistentState { get; }
        public IEpisodicMemoryActor EpisodicMemory { get; } = Substitute.For<IEpisodicMemoryActor>();
        public ISkillMemoryActor SkillMemory { get; } = Substitute.For<ISkillMemoryActor>();
        public AgentActor Actor { get; }

        public Fixture()
        {
            var state = new AgentState
            {
                AgentId = $"{TestWorkspaceId}/{TestAgentName}",
                WorkspaceId = TestWorkspaceId,
                AgentName = TestAgentName
            };
            PersistentState = Substitute.For<IActorState<AgentState>>();
            PersistentState.State.Returns(state);
            PersistentState.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            PersistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

            EpisodicMemory.StoreEpisodeAsync(Arg.Any<Episode>())
                .Returns(call => Task.FromResult(call.Arg<Episode>()));
            SkillMemory.SuggestSkillAsync(Arg.Any<SkillDocument>(), Arg.Any<string?>())
                .Returns(call => Task.FromResult(new SkillSuggestion
                {
                    Skill = call.Arg<SkillDocument>(),
                    SourceTaskId = call.ArgAt<string?>(1)
                }));

            var actors = Substitute.For<IVirtualActorProvider>();
            actors.GetActor<IEpisodicMemoryActor>(Arg.Any<VirtualActorId>()).Returns(EpisodicMemory);
            actors.GetActor<ISkillMemoryActor>(Arg.Any<VirtualActorId>()).Returns(SkillMemory);
            actors.GetActor<IProofVerifierActor>(Arg.Any<VirtualActorId>()).Returns(Substitute.For<IProofVerifierActor>());

            Actor = new AgentActor(
                actors,
                Substitute.For<IAgentChatPipeline>(),
                Substitute.For<ILifecycleManager>(),
                Substitute.For<IEventBus>(),
                TimeProvider.System,
                Substitute.For<ILogger<AgentActor>>(),
                PersistentState);
        }
    }

    private static AgentDefinition CreateDefinition() =>
        new() { Model = "test-model", MaxConcurrentTasks = 2, Tools = [] };

    private static ProofOfWork TwoItemProof() => new()
    {
        Items =
        [
            new ProofItem { Type = ProofType.PullRequest, Label = "PR-1", Value = "Merged" },
            new ProofItem { Type = ProofType.CiStatus, Label = "ci", Value = "green" }
        ]
    };

    [Fact]
    public async Task ReviewTaskAsync_Accepted_StoresEpisode()
    {
        var fx = new Fixture();
        await fx.Actor.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        var task = await fx.Actor.SubmitTaskAsync("Ship the canary release");
        await fx.Actor.CompleteTaskAsync(task.TaskId, success: true, TwoItemProof());

        await fx.Actor.ReviewTaskAsync(task.TaskId, accepted: true, "looks good");

        await fx.EpisodicMemory.Received(1).StoreEpisodeAsync(
            Arg.Is<Episode>(e =>
                e.Title == "Ship the canary release" &&
                e.SourceTaskId == task.TaskId.ToString() &&
                e.AgentName == TestAgentName));
    }

    [Fact]
    public async Task ReviewTaskAsync_Accepted_AdvancesLastEpisodeHistoryIndex()
    {
        var fx = new Fixture();
        await fx.Actor.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        var state = fx.PersistentState.State;
        state.History.Add(new ConversationMessage { Role = "user", Content = "ship it" });
        state.History.Add(new ConversationMessage { Role = "assistant", Content = "on it" });
        var task = await fx.Actor.SubmitTaskAsync("Ship the canary release");
        await fx.Actor.CompleteTaskAsync(task.TaskId, success: true, TwoItemProof());

        await fx.Actor.ReviewTaskAsync(task.TaskId, accepted: true);

        state.LastEpisodeHistoryIndex.ShouldBe(state.History.Count);
    }

    [Fact]
    public async Task ReviewTaskAsync_Rejected_DoesNotStoreEpisode()
    {
        var fx = new Fixture();
        await fx.Actor.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        var task = await fx.Actor.SubmitTaskAsync("Ship the canary release");
        await fx.Actor.CompleteTaskAsync(task.TaskId, success: true, TwoItemProof());

        await fx.Actor.ReviewTaskAsync(task.TaskId, accepted: false, "broken");

        await fx.EpisodicMemory.DidNotReceive().StoreEpisodeAsync(Arg.Any<Episode>());
    }

    [Fact]
    public async Task ReviewTaskAsync_StoreEpisodeThrows_DoesNotPropagateOrAdvanceIndex()
    {
        var fx = new Fixture();
        fx.EpisodicMemory.StoreEpisodeAsync(Arg.Any<Episode>())
            .Returns(Task.FromException<Episode>(new InvalidOperationException("episodic store down")));
        await fx.Actor.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        var task = await fx.Actor.SubmitTaskAsync("Ship the canary release");
        await fx.Actor.CompleteTaskAsync(task.TaskId, success: true, TwoItemProof());

        // Must not throw — extraction failures are isolated.
        await fx.Actor.ReviewTaskAsync(task.TaskId, accepted: true);

        // Index stays at 0 so the next attempt re-extracts the same slice.
        fx.PersistentState.State.LastEpisodeHistoryIndex.ShouldBe(0);
    }

    [Fact]
    public async Task DeactivateAsync_WithUnflushedHistory_StoresSessionEpisode()
    {
        var fx = new Fixture();
        await fx.Actor.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        var state = fx.PersistentState.State;
        state.History.Add(new ConversationMessage { Role = "user", Content = "How do we deploy?" });
        state.History.Add(new ConversationMessage { Role = "assistant", Content = "Canary first." });

        await fx.Actor.DeactivateAsync();

        await fx.EpisodicMemory.Received(1).StoreEpisodeAsync(
            Arg.Is<Episode>(e =>
                e.SourceTaskId == null &&
                e.AgentName == TestAgentName &&
                e.Title == "How do we deploy?"));
        state.LastEpisodeHistoryIndex.ShouldBe(2);
    }

    [Fact]
    public async Task DeactivateAsync_WithNoUnflushedHistory_DoesNotStore()
    {
        var fx = new Fixture();
        await fx.Actor.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        var state = fx.PersistentState.State;
        state.History.Add(new ConversationMessage { Role = "user", Content = "already flushed" });
        state.LastEpisodeHistoryIndex = 1;

        await fx.Actor.DeactivateAsync();

        await fx.EpisodicMemory.DidNotReceive().StoreEpisodeAsync(Arg.Any<Episode>());
    }
}
