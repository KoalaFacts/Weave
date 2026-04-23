using Microsoft.Extensions.Logging;
using Weave.Agents.Events;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Agents.Pipeline;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Models;

namespace Weave.Agents.Tests;

/// <summary>
/// Covers <see cref="AgentGrain"/> branches that <c>AgentGrainTests</c> doesn't
/// reach: <c>SendAsync</c> (happy path + inactive-throws), activation failure
/// path (error event + state rollback), and tool connect/disconnect idempotence.
/// </summary>
public sealed class AgentGrainBranchTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");
    private const string TestAgentName = "researcher";

    private static IPersistentState<AgentState> CreatePersistentState()
    {
        var state = new AgentState
        {
            AgentId = $"{TestWorkspaceId}/{TestAgentName}",
            WorkspaceId = TestWorkspaceId,
            AgentName = TestAgentName
        };
        var ps = Substitute.For<IPersistentState<AgentState>>();
        ps.State.Returns(state);
        ps.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        ps.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        ps.WriteStateAsync().Returns(Task.CompletedTask);
        return ps;
    }

    private sealed class Fixture
    {
        public IGrainFactory GrainFactory { get; } = Substitute.For<IGrainFactory>();
        public IAgentChatPipeline ChatPipeline { get; } = Substitute.For<IAgentChatPipeline>();
        public ILifecycleManager Lifecycle { get; } = Substitute.For<ILifecycleManager>();
        public IEventBus EventBus { get; } = Substitute.For<IEventBus>();
        public IPersistentState<AgentState> State { get; } = CreatePersistentState();
        public AgentGrain Grain { get; }

        public Fixture()
        {
            GrainFactory.GetGrain<ISkillMemoryGrain>(Arg.Any<string>(), null)
                .Returns(Substitute.For<ISkillMemoryGrain>());
            GrainFactory.GetGrain<IProofVerifierGrain>(Arg.Any<string>(), null)
                .Returns(Substitute.For<IProofVerifierGrain>());

            Grain = new AgentGrain(
                GrainFactory, ChatPipeline, Lifecycle, EventBus, TimeProvider.System,
                Substitute.For<ILogger<AgentGrain>>(), State);
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
            () => fx.Grain.SendAsync(new AgentMessage { Role = "user", Content = "hi" }));
    }

    [Fact]
    public async Task SendAsync_WhenActive_DelegatesToPipelineAndReturnsResponse()
    {
        var fx = new Fixture();
        await fx.Grain.ActivateAgentAsync(TestWorkspaceId, Def());
        var expected = new AgentChatResponse { Content = "ok", ConversationId = "c1", UsedTools = false };
        fx.ChatPipeline.ExecuteAsync(Arg.Any<AgentState>(), Arg.Any<AgentMessage>()).Returns(expected);

        var result = await fx.Grain.SendAsync(new AgentMessage { Role = "user", Content = "hi" });

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
            () => fx.Grain.ActivateAgentAsync(TestWorkspaceId, Def()));

        fx.State.State.Status.ShouldBe(AgentStatus.Error);
        fx.State.State.ErrorMessage.ShouldNotBeNull();
        fx.State.State.ErrorMessage.ShouldContain("hook refused activation");
        await fx.EventBus.Received().PublishAsync(Arg.Any<AgentErrorEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConnectToolAsync_CalledTwice_DoesNotDuplicate()
    {
        var fx = new Fixture();

        await fx.Grain.ConnectToolAsync("shell");
        await fx.Grain.ConnectToolAsync("shell");

        fx.State.State.ConnectedTools.ShouldBe(["shell"]);
    }

    [Fact]
    public async Task DisconnectToolAsync_UnknownTool_IsNoOp()
    {
        var fx = new Fixture();

        await fx.Grain.DisconnectToolAsync("never-connected");

        fx.State.State.ConnectedTools.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConnectToolAsync_PersistsInReturnedState()
    {
        var fx = new Fixture();

        await fx.Grain.ConnectToolAsync("shell");
        await fx.Grain.ConnectToolAsync("search");
        await fx.Grain.DisconnectToolAsync("shell");

        fx.State.State.ConnectedTools.ShouldBe(["search"]);
    }

    [Fact]
    public async Task ActivateAgentAsync_AlreadyBusy_ReturnsCurrentStateWithoutReactivating()
    {
        var fx = new Fixture();
        fx.State.State.Status = AgentStatus.Busy;
        fx.State.State.Model = "preserved-model";

        var result = await fx.Grain.ActivateAgentAsync(TestWorkspaceId, Def());

        result.Status.ShouldBe(AgentStatus.Busy);
        result.Model.ShouldBe("preserved-model", "activating when already Busy is a no-op; previous model kept");
    }

    [Fact]
    public async Task GetStateAsync_ReturnsCurrentState()
    {
        var fx = new Fixture();
        fx.State.State.Status = AgentStatus.Active;

        var state = await fx.Grain.GetStateAsync();

        state.Status.ShouldBe(AgentStatus.Active);
    }

    [Fact]
    public async Task DeactivateAsync_LifecycleHookThrows_LogsAndRethrows()
    {
        var fx = new Fixture();
        await fx.Grain.ActivateAgentAsync(TestWorkspaceId, Def());
        fx.Lifecycle.ClearReceivedCalls();
        fx.Lifecycle.RunHooksAsync(LifecyclePhase.AgentDeactivating, Arg.Any<LifecycleContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("deactivation hook refused")));

        await Should.ThrowAsync<InvalidOperationException>(() => fx.Grain.DeactivateAsync());

        fx.State.State.Status.ShouldBe(AgentStatus.Error);
    }

    [Fact]
    public async Task ReviewTaskAsync_AcceptedWithMultiStepProof_AttemptsSkillExtract()
    {
        var fx = new Fixture();
        var skillGrain = Substitute.For<ISkillMemoryGrain>();
        fx.GrainFactory.GetGrain<ISkillMemoryGrain>(Arg.Any<string>(), null).Returns(skillGrain);

        await fx.Grain.ActivateAgentAsync(TestWorkspaceId, Def());
        var task = await fx.Grain.SubmitTaskAsync("multi-step task");
        var proof = new ProofOfWork
        {
            Items =
            [
                new ProofItem { Type = ProofType.Custom, Label = "step-1", Value = "v1" },
                new ProofItem { Type = ProofType.Custom, Label = "step-2", Value = "v2" }
            ]
        };
        await fx.Grain.CompleteTaskAsync(task.TaskId, success: true, proof);

        await fx.Grain.ReviewTaskAsync(task.TaskId, accepted: true);

        await skillGrain.Received(1).StoreSkillAsync(Arg.Any<SkillDocument>());
    }

    [Fact]
    public async Task ReviewTaskAsync_AcceptedAndSkillStoreThrows_LogsWarningAndDoesNotPropagate()
    {
        var fx = new Fixture();
        var skillGrain = Substitute.For<ISkillMemoryGrain>();
        skillGrain.StoreSkillAsync(Arg.Any<SkillDocument>())
            .Returns(Task.FromException<SkillDocument>(new InvalidOperationException("store broken")));
        fx.GrainFactory.GetGrain<ISkillMemoryGrain>(Arg.Any<string>(), null).Returns(skillGrain);

        await fx.Grain.ActivateAgentAsync(TestWorkspaceId, Def());
        var task = await fx.Grain.SubmitTaskAsync("multi-step task");
        await fx.Grain.CompleteTaskAsync(task.TaskId, success: true, new ProofOfWork
        {
            Items =
            [
                new ProofItem { Type = ProofType.Custom, Label = "a", Value = "x" },
                new ProofItem { Type = ProofType.Custom, Label = "b", Value = "y" }
            ]
        });

        // Should not throw — skill extraction failures are logged, not propagated.
        await fx.Grain.ReviewTaskAsync(task.TaskId, accepted: true);
    }
}
