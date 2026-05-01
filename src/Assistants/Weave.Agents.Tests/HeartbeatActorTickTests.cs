using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Actors;
using Weave.Agents.Heartbeat;
using Weave.Agents.Models;
using Weave.Shared.Ids;
using Weave.Shared.VirtualActors;

namespace Weave.Agents.Tests;

/// <summary>
/// Exercises <see cref="HeartbeatTickRunner"/> directly against
/// a fake agent actor. Covers every tick-path branch — active agent,
/// inactive agent, max-concurrent retry, per-task failure, state update —
/// without needing an Orleans actor runtime.
/// </summary>
public sealed class HeartbeatActorTickTests
{
    private sealed class Fixture
    {
        public IVirtualActorProvider ActorProvider { get; } = Substitute.For<IVirtualActorProvider>();
        public IAgentActor AgentActor { get; } = Substitute.For<IAgentActor>();
        public Microsoft.Extensions.Time.Testing.FakeTimeProvider Time { get; } =
            new(new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero));
        public HeartbeatTickRunner Runner { get; }

        public Fixture(AgentStatus agentStatus = AgentStatus.Active)
        {
            Runner = new HeartbeatTickRunner(
                ActorProvider,
                Time,
                NullLogger<HeartbeatTickRunner>.Instance,
                new HeartbeatSchedule());
            AgentActor.GetStateAsync().Returns(new AgentState
            {
                AgentId = "ws-1/agent-1",
                WorkspaceId = WorkspaceId.From("ws-1"),
                AgentName = "agent-1",
                Status = agentStatus
            });
            AgentActor.SubmitTaskAsync(Arg.Any<string>()).Returns(ci => new AgentTaskInfo
            {
                TaskId = AgentTaskId.New(),
                Description = ci.Arg<string>()
            });
            AgentActor.SendAsync(Arg.Any<AgentMessage>()).Returns(new AgentChatResponse
            {
                Content = "done",
                ConversationId = "c1",
                UsedTools = false
            });
            ActorProvider.GetActor<IAgentActor>(Arg.Any<VirtualActorId>()).Returns(AgentActor);
        }

        public Task<HeartbeatState> PerformTickAsync(HeartbeatState state, string agentKey = "ws-1/agent-1")
            => Runner.ExecuteAsync(state, agentKey, CancellationToken.None);
    }

    private static HeartbeatState RunningStateWithTasks(params string[] tasks) => new()
    {
        IsRunning = true,
        Config = new HeartbeatConfig { Enabled = true, Cron = "*/5 * * * *", Tasks = [.. tasks] }
    };

    [Fact]
    public async Task Tick_UnknownAgentKey_ReturnsWithoutCallingActorFactory()
    {
        var fx = new Fixture();
        var result = await fx.PerformTickAsync(RunningStateWithTasks("task"), agentKey: "unknown-agent");

        fx.ActorProvider.DidNotReceive().GetActor<IAgentActor>(Arg.Any<VirtualActorId>());
        result.ExecutionCount.ShouldBe(0);
    }

    [Fact]
    public async Task Tick_AgentInactive_SkipsTasksAndDoesNotAdvanceState()
    {
        var fx = new Fixture(AgentStatus.Idle);
        var result = await fx.PerformTickAsync(RunningStateWithTasks("review PRs"));

        await fx.AgentActor.DidNotReceive().SubmitTaskAsync(Arg.Any<string>());
        result.ExecutionCount.ShouldBe(0, "inactive agents short-circuit before the state update");
    }

    [Fact]
    public async Task Tick_ActiveAgent_SubmitsSendsCompletesWithProof()
    {
        var fx = new Fixture();
        var result = await fx.PerformTickAsync(RunningStateWithTasks("review PRs"));

        await fx.AgentActor.Received(1).SubmitTaskAsync("[Heartbeat] review PRs");
        await fx.AgentActor.Received(1).SendAsync(Arg.Any<AgentMessage>());
        await fx.AgentActor.Received(1).CompleteTaskAsync(
            Arg.Any<AgentTaskId>(),
            success: true,
            Arg.Any<ProofOfWork>());
        result.ExecutionCount.ShouldBe(1);
    }

    [Fact]
    public async Task Tick_TruncatesLongResponseBodyToProofValue()
    {
        var fx = new Fixture();
        var longContent = new string('x', 500);
        fx.AgentActor.SendAsync(Arg.Any<AgentMessage>()).Returns(new AgentChatResponse
        {
            Content = longContent,
            ConversationId = "c1",
            UsedTools = false
        });

        ProofOfWork? captured = null;
        await fx.AgentActor.CompleteTaskAsync(
            Arg.Any<AgentTaskId>(),
            Arg.Any<bool>(),
            Arg.Do<ProofOfWork>(p => captured = p));

        await fx.PerformTickAsync(RunningStateWithTasks("big task"));

        captured.ShouldNotBeNull();
        captured!.Items.ShouldHaveSingleItem();
        captured.Items[0].Value.Length.ShouldBe(200);
    }

    [Fact]
    public async Task Tick_SendAsyncThrows_MarksTaskFailedWithProof()
    {
        var fx = new Fixture();
        fx.AgentActor.SendAsync(Arg.Any<AgentMessage>())
            .Returns(Task.FromException<AgentChatResponse>(new InvalidOperationException("llm failed")));

        await fx.PerformTickAsync(RunningStateWithTasks("bound-to-fail"));

        await fx.AgentActor.Received(1).CompleteTaskAsync(
            Arg.Any<AgentTaskId>(),
            success: false,
            Arg.Any<ProofOfWork>());
    }

    [Fact]
    public async Task Tick_MaxConcurrentError_BreaksLoopWithoutCompleting()
    {
        var fx = new Fixture();
        fx.AgentActor.SubmitTaskAsync(Arg.Any<string>())
            .Returns(Task.FromException<AgentTaskInfo>(new InvalidOperationException("Agent max concurrent tasks reached")));

        await fx.PerformTickAsync(RunningStateWithTasks("one", "two", "three"));

        // Only one SubmitTaskAsync should fire — the break contract:
        // as soon as one task hits max concurrent, stop submitting more this tick.
        await fx.AgentActor.Received(1).SubmitTaskAsync(Arg.Any<string>());
        await fx.AgentActor.DidNotReceive().CompleteTaskAsync(
            Arg.Any<AgentTaskId>(),
            Arg.Any<bool>(),
            Arg.Any<ProofOfWork>());
    }

    [Fact]
    public async Task Tick_AfterTimeAdvance_LastRunMatchesNewClock()
    {
        var fx = new Fixture();
        var expected = fx.Time.GetUtcNow().AddMinutes(5);
        fx.Time.Advance(TimeSpan.FromMinutes(5));

        var result = await fx.PerformTickAsync(RunningStateWithTasks());

        result.LastRun.ShouldBe(expected);
        result.NextRun.ShouldBe(expected.AddMinutes(5));
    }
}