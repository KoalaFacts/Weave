using Microsoft.Extensions.Logging;
using Weave.Agents.Actors;
using Weave.Agents.Events;
using Weave.Agents.Models;
using Weave.Agents.Pipeline;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Models;

namespace Weave.Agents.Tests;

/// <summary>
/// Covers <see cref="AgentActor"/> branches that <c>AgentActorTests</c> doesn't
/// reach: <c>SendAsync</c> (happy path + inactive-throws), activation failure
/// path (error event + state rollback), and tool connect/disconnect idempotence.
/// </summary>
public sealed class AgentActorBranchTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");
    private const string TestAgentName = "researcher";

    private static IActorState<AgentState> CreatePersistentState()
    {
        var state = new AgentState
        {
            AgentId = $"{TestWorkspaceId}/{TestAgentName}",
            WorkspaceId = TestWorkspaceId,
            AgentName = TestAgentName
        };
        var ps = Substitute.For<IActorState<AgentState>>();
        ps.State.Returns(state);
        ps.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        ps.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        ps.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return ps;
    }

    private sealed class Fixture
    {
        public IVirtualActorProvider ActorProvider { get; } = Substitute.For<IVirtualActorProvider>();
        public IAgentChatPipeline ChatPipeline { get; } = Substitute.For<IAgentChatPipeline>();
        public ILifecycleManager Lifecycle { get; } = Substitute.For<ILifecycleManager>();
        public IEventBus EventBus { get; } = Substitute.For<IEventBus>();
        public IActorState<AgentState> State { get; } = CreatePersistentState();
        public AgentActor Actor { get; }

        public Fixture()
        {
            ActorProvider.GetActor<ISkillMemoryActor>(Arg.Any<VirtualActorId>())
                .Returns(Substitute.For<ISkillMemoryActor>());
            ActorProvider.GetActor<IProofVerifierActor>(Arg.Any<VirtualActorId>())
                .Returns(Substitute.For<IProofVerifierActor>());

            Actor = new AgentActor(
                ActorProvider, ChatPipeline, Lifecycle, EventBus, TimeProvider.System,
                Substitute.For<ILogger<AgentActor>>(), State);
        }
    }

    private static AgentDefinition Def() => new()
    {
        Model = "test-model",
        MaxConcurrentTasks = 2,
        Tools = []
    };

    [Fact]
    public async Task SendAsync_WhenNotActive_ThrowsInvalidOperation()
    {
        var fx = new Fixture();
        // State defaults to Idle

        await Should.ThrowAsync<InvalidOperationException>(
            () => fx.Actor.SendAsync(new AgentMessage { Role = "user", Content = "hi" }));
    }

    [Fact]
    public async Task SendAsync_WhenActive_DelegatesToPipelineAndReturnsResponse()
    {
        var fx = new Fixture();
        await fx.Actor.ActivateAgentAsync(TestWorkspaceId, Def());
        var expected = new AgentChatResponse { Content = "ok", ConversationId = "c1", UsedTools = false };
        fx.ChatPipeline.ExecuteAsync(Arg.Any<AgentState>(), Arg.Any<AgentMessage>()).Returns(expected);

        var result = await fx.Actor.SendAsync(new AgentMessage { Role = "user", Content = "hi" });

        result.ShouldBe(expected);
        await fx.ChatPipeline.Received(1).ExecuteAsync(Arg.Any<AgentState>(), Arg.Any<AgentMessage>());
    }

    [Fact]
    public async Task ActivateAgentAsync_LifecycleHookThrows_StateGoesToErrorAndErrorEventPublished()
    {
        var fx = new Fixture();
        fx.Lifecycle.RunHooksAsync(LifecyclePhase.AgentActivating, Arg.Any<LifecycleContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("hook refused activation")));

        await Should.ThrowAsync<InvalidOperationException>(
            () => fx.Actor.ActivateAgentAsync(TestWorkspaceId, Def()));

        fx.State.State.Status.ShouldBe(AgentStatus.Error);
        fx.State.State.ErrorMessage.ShouldNotBeNull();
        fx.State.State.ErrorMessage.ShouldContain("hook refused activation");
        await fx.EventBus.Received().PublishAsync(Arg.Any<AgentErrorEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConnectToolAsync_CalledTwice_DoesNotDuplicate()
    {
        var fx = new Fixture();

        await fx.Actor.ConnectToolAsync("shell");
        await fx.Actor.ConnectToolAsync("shell");

        fx.State.State.ConnectedTools.ShouldBe(["shell"]);
    }

    [Fact]
    public async Task DisconnectToolAsync_UnknownTool_IsNoOp()
    {
        var fx = new Fixture();

        await fx.Actor.DisconnectToolAsync("never-connected");

        fx.State.State.ConnectedTools.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConnectToolAsync_PersistsInReturnedState()
    {
        var fx = new Fixture();

        await fx.Actor.ConnectToolAsync("shell");
        await fx.Actor.ConnectToolAsync("search");
        await fx.Actor.DisconnectToolAsync("shell");

        fx.State.State.ConnectedTools.ShouldBe(["search"]);
    }

    [Fact]
    public async Task ActivateAgentAsync_AlreadyBusy_ReturnsCurrentStateWithoutReactivating()
    {
        var fx = new Fixture();
        fx.State.State.Status = AgentStatus.Busy;
        fx.State.State.Model = "preserved-model";

        var result = await fx.Actor.ActivateAgentAsync(TestWorkspaceId, Def());

        result.Status.ShouldBe(AgentStatus.Busy);
        result.Model.ShouldBe("preserved-model", "activating when already Busy is a no-op; previous model kept");
    }

    [Fact]
    public async Task GetStateAsync_ReturnsCurrentState()
    {
        var fx = new Fixture();
        fx.State.State.Status = AgentStatus.Active;

        var state = await fx.Actor.GetStateAsync();

        state.Status.ShouldBe(AgentStatus.Active);
    }

    [Fact]
    public async Task DeactivateAsync_LifecycleHookThrows_LogsAndRethrows()
    {
        var fx = new Fixture();
        await fx.Actor.ActivateAgentAsync(TestWorkspaceId, Def());
        fx.Lifecycle.ClearReceivedCalls();
        fx.Lifecycle.RunHooksAsync(LifecyclePhase.AgentDeactivating, Arg.Any<LifecycleContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("deactivation hook refused")));

        await Should.ThrowAsync<InvalidOperationException>(() => fx.Actor.DeactivateAsync());

        fx.State.State.Status.ShouldBe(AgentStatus.Error);
    }

    [Fact]
    public async Task ReviewTaskAsync_AcceptedWithMultiStepProof_SuggestsSkill()
    {
        var fx = new Fixture();
        var skillActor = Substitute.For<ISkillMemoryActor>();
        skillActor.SuggestSkillAsync(Arg.Any<SkillDocument>(), Arg.Any<string?>())
            .Returns(callInfo => Task.FromResult(new SkillSuggestion
            {
                Skill = callInfo.Arg<SkillDocument>(),
                SourceTaskId = callInfo.ArgAt<string?>(1)
            }));
        fx.ActorProvider.GetActor<ISkillMemoryActor>(Arg.Any<VirtualActorId>()).Returns(skillActor);

        await fx.Actor.ActivateAgentAsync(TestWorkspaceId, Def());
        var task = await fx.Actor.SubmitTaskAsync("multi-step task");
        var proof = new ProofOfWork
        {
            Items =
            [
                new ProofItem { Type = ProofType.Custom, Label = "step-1", Value = "v1" },
                new ProofItem { Type = ProofType.Custom, Label = "step-2", Value = "v2" }
            ]
        };
        await fx.Actor.CompleteTaskAsync(task.TaskId, success: true, proof);

        await fx.Actor.ReviewTaskAsync(task.TaskId, accepted: true);

        await skillActor.Received(1).SuggestSkillAsync(Arg.Any<SkillDocument>(), task.TaskId.ToString());
        await skillActor.DidNotReceive().StoreSkillAsync(Arg.Any<SkillDocument>());
    }

    [Fact]
    public async Task ReviewTaskAsync_AcceptedAndSkillSuggestionThrows_LogsWarningAndDoesNotPropagate()
    {
        var fx = new Fixture();
        var skillActor = Substitute.For<ISkillMemoryActor>();
        skillActor.SuggestSkillAsync(Arg.Any<SkillDocument>(), Arg.Any<string?>())
            .Returns(Task.FromException<SkillSuggestion>(new InvalidOperationException("suggestion broken")));
        fx.ActorProvider.GetActor<ISkillMemoryActor>(Arg.Any<VirtualActorId>()).Returns(skillActor);

        await fx.Actor.ActivateAgentAsync(TestWorkspaceId, Def());
        var task = await fx.Actor.SubmitTaskAsync("multi-step task");
        await fx.Actor.CompleteTaskAsync(task.TaskId, success: true, new ProofOfWork
        {
            Items =
            [
                new ProofItem { Type = ProofType.Custom, Label = "a", Value = "x" },
                new ProofItem { Type = ProofType.Custom, Label = "b", Value = "y" }
            ]
        });

        // Should not throw — skill extraction failures are logged, not propagated.
        await fx.Actor.ReviewTaskAsync(task.TaskId, accepted: true);
    }
}
