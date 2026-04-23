using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Grains;
using Weave.Agents.Heartbeat;
using Weave.Agents.Models;
using Weave.Agents.Tests.TestCluster;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

/// <summary>
/// Exercises <see cref="HeartbeatGrain"/> against a real Orleans cluster with
/// a <c>FakeTimeProvider</c> injected via DI. Timer-driven paths that used
/// to be unreachable (direct-instantiation tests couldn't hit
/// <c>RegisterGrainTimer</c> / <c>OnHeartbeatTick</c>) are now deterministic.
/// </summary>
[Collection(nameof(WeaveClusterCollection))]
public sealed class HeartbeatGrainClusterTests
{
    private readonly WeaveTestCluster _cluster;

    public HeartbeatGrainClusterTests(WeaveTestCluster cluster) => _cluster = cluster;

    private static string NewGrainKey() => $"ws-{Guid.NewGuid():N}/agent-{Guid.NewGuid():N}";

    [Fact]
    public async Task StartAsync_EnabledConfig_NextRunMatchesFakeClock()
    {
        var grain = _cluster.GrainFactory.GetGrain<IHeartbeatGrain>(NewGrainKey());
        var config = new HeartbeatConfig
        {
            Enabled = true,
            Cron = "*/5 * * * *",
            Tasks = ["review open PRs"]
        };

        await grain.StartAsync(config);
        var state = await grain.GetStateAsync();

        state.IsRunning.ShouldBeTrue();
        state.NextRun.ShouldNotBeNull();
        // The fixture's FakeTimeProvider starts at 2026-04-19 12:00:00 UTC —
        // cron "*/5 * * * *" parses to 5-minute interval, so NextRun must be
        // exactly 5 minutes after the fixture's fake "now".
        state.NextRun.Value.ShouldBe(_cluster.Time.GetUtcNow().AddMinutes(5));
    }

    [Fact]
    public async Task StartAsync_DisabledConfig_DoesNotMarkRunning()
    {
        var grain = _cluster.GrainFactory.GetGrain<IHeartbeatGrain>(NewGrainKey());
        var config = new HeartbeatConfig { Enabled = false, Cron = "*/30 * * * *" };

        await grain.StartAsync(config);
        var state = await grain.GetStateAsync();

        state.IsRunning.ShouldBeFalse();
        state.NextRun.ShouldBeNull();
    }

    [Fact]
    public async Task StartAsync_CalledTwice_SecondCallIsNoOp()
    {
        var grain = _cluster.GrainFactory.GetGrain<IHeartbeatGrain>(NewGrainKey());
        var config = new HeartbeatConfig { Enabled = true, Cron = "*/1440 * * * *" };

        await grain.StartAsync(config);
        var firstNextRun = (await grain.GetStateAsync()).NextRun;
        await grain.StartAsync(config with { Cron = "*/60 * * * *" });
        var secondNextRun = (await grain.GetStateAsync()).NextRun;

        secondNextRun.ShouldBe(firstNextRun);
    }

    [Fact]
    public async Task StopAsync_AfterStart_ClearsRunningAndNextRun()
    {
        var grain = _cluster.GrainFactory.GetGrain<IHeartbeatGrain>(NewGrainKey());
        await grain.StartAsync(new HeartbeatConfig { Enabled = true, Cron = "*/1440 * * * *" });

        await grain.StopAsync();
        var state = await grain.GetStateAsync();

        state.IsRunning.ShouldBeFalse();
        state.NextRun.ShouldBeNull();
    }

    [Fact]
    public async Task StopAsync_WithoutStart_IsSafe()
    {
        var grain = _cluster.GrainFactory.GetGrain<IHeartbeatGrain>(NewGrainKey());

        await grain.StopAsync();
        var state = await grain.GetStateAsync();

        state.IsRunning.ShouldBeFalse();
    }
}

/// <summary>
/// Exercises <see cref="HeartbeatGrain.PerformTickAsync"/> directly against
/// a fake agent grain. Covers every tick-path branch — active agent,
/// inactive agent, max-concurrent retry, per-task failure, state update —
/// without needing an Orleans grain runtime. The tick logic was factored
/// into a pure static method per the repo's "promote private methods to
/// internal for testability" convention (see <c>CLAUDE.md</c> and
/// <c>ProofValidatorGrain</c>).
/// </summary>
public sealed class HeartbeatGrainTickTests
{
    private sealed class Fixture
    {
        public IGrainFactory GrainFactory { get; } = Substitute.For<IGrainFactory>();
        public IAgentGrain AgentGrain { get; } = Substitute.For<IAgentGrain>();
        public Microsoft.Extensions.Time.Testing.FakeTimeProvider Time { get; } =
            new(new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero));

        public Fixture(AgentStatus agentStatus = AgentStatus.Active)
        {
            AgentGrain.GetStateAsync().Returns(new AgentState
            {
                AgentId = "ws-1/agent-1",
                WorkspaceId = WorkspaceId.From("ws-1"),
                AgentName = "agent-1",
                Status = agentStatus
            });
            AgentGrain.SubmitTaskAsync(Arg.Any<string>()).Returns(ci => new AgentTaskInfo
            {
                TaskId = AgentTaskId.New(),
                Description = ci.Arg<string>()
            });
            AgentGrain.SendAsync(Arg.Any<AgentMessage>()).Returns(new AgentChatResponse
            {
                Content = "done",
                ConversationId = "c1",
                UsedTools = false
            });
            GrainFactory.GetGrain<IAgentGrain>(Arg.Any<string>(), null).Returns(AgentGrain);
        }

        public Task<HeartbeatState> PerformTickAsync(HeartbeatState state, string agentKey = "ws-1/agent-1")
            => HeartbeatGrain.PerformTickAsync(state, agentKey, GrainFactory, Time, NullLogger<HeartbeatGrain>.Instance, CancellationToken.None);
    }

    private static HeartbeatState RunningStateWithTasks(params string[] tasks) => new()
    {
        IsRunning = true,
        Config = new HeartbeatConfig { Enabled = true, Cron = "*/5 * * * *", Tasks = [.. tasks] }
    };

    [Fact]
    public async Task Tick_UnknownAgentKey_ReturnsWithoutCallingGrainFactory()
    {
        var fx = new Fixture();
        var result = await fx.PerformTickAsync(RunningStateWithTasks("task"), agentKey: "unknown-agent");

        fx.GrainFactory.DidNotReceive().GetGrain<IAgentGrain>(Arg.Any<string>(), null);
        result.ExecutionCount.ShouldBe(0);
    }

    [Fact]
    public async Task Tick_AgentInactive_SkipsTasksAndDoesNotAdvanceState()
    {
        var fx = new Fixture(AgentStatus.Idle);
        var result = await fx.PerformTickAsync(RunningStateWithTasks("review PRs"));

        await fx.AgentGrain.DidNotReceive().SubmitTaskAsync(Arg.Any<string>());
        result.ExecutionCount.ShouldBe(0, "inactive agents short-circuit before the state update");
    }

    [Fact]
    public async Task Tick_ActiveAgent_SubmitsSendsCompletesWithProof()
    {
        var fx = new Fixture();
        var result = await fx.PerformTickAsync(RunningStateWithTasks("review PRs"));

        await fx.AgentGrain.Received(1).SubmitTaskAsync("[Heartbeat] review PRs");
        await fx.AgentGrain.Received(1).SendAsync(Arg.Any<AgentMessage>());
        await fx.AgentGrain.Received(1).CompleteTaskAsync(
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
        fx.AgentGrain.SendAsync(Arg.Any<AgentMessage>()).Returns(new AgentChatResponse
        {
            Content = longContent,
            ConversationId = "c1",
            UsedTools = false
        });

        ProofOfWork? captured = null;
        await fx.AgentGrain.CompleteTaskAsync(
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
        fx.AgentGrain.SendAsync(Arg.Any<AgentMessage>())
            .Returns(Task.FromException<AgentChatResponse>(new InvalidOperationException("llm failed")));

        await fx.PerformTickAsync(RunningStateWithTasks("bound-to-fail"));

        await fx.AgentGrain.Received(1).CompleteTaskAsync(
            Arg.Any<AgentTaskId>(),
            success: false,
            Arg.Any<ProofOfWork>());
    }

    [Fact]
    public async Task Tick_MaxConcurrentError_BreaksLoopWithoutCompleting()
    {
        var fx = new Fixture();
        fx.AgentGrain.SubmitTaskAsync(Arg.Any<string>())
            .Returns(Task.FromException<AgentTaskInfo>(new InvalidOperationException("Agent max concurrent tasks reached")));

        await fx.PerformTickAsync(RunningStateWithTasks("one", "two", "three"));

        // Only one SubmitTaskAsync should fire — the break contract:
        // as soon as one task hits max concurrent, stop submitting more this tick.
        await fx.AgentGrain.Received(1).SubmitTaskAsync(Arg.Any<string>());
        await fx.AgentGrain.DidNotReceive().CompleteTaskAsync(
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
