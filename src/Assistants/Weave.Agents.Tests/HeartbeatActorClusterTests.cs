using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Channels;
using Weave.Agents.Heartbeat;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.Tests.TestCluster;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

/// <summary>
/// Exercises <see cref="HeartbeatActor"/> against a real Orleans cluster with
/// a <c>FakeTimeProvider</c> injected via DI. Timer-driven paths that used
/// to be unreachable (direct-instantiation tests couldn't hit
/// the Orleans timer API / <c>OnHeartbeatTick</c>) are now deterministic.
/// </summary>
[Collection(nameof(WeaveClusterFixtureDefinition))]
public sealed class HeartbeatActorClusterTests
{
    private readonly WeaveTestCluster _cluster;

    public HeartbeatActorClusterTests(WeaveTestCluster cluster) => _cluster = cluster;

    private static VirtualActorId NewActorKey() => VirtualActorId.From($"ws-{Guid.NewGuid():N}/agent-{Guid.NewGuid():N}");

    [Fact]
    public async Task StartAsync_EnabledConfig_NextRunMatchesFakeClock()
    {
        var actor = _cluster.ActorProvider.GetActor<IHeartbeatActor>(NewActorKey());
        var config = new HeartbeatConfig
        {
            Enabled = true,
            Cron = "*/5 * * * *",
            Tasks = ["review open PRs"]
        };

        await actor.StartAsync(config);
        var state = await actor.GetStateAsync();

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
        var actor = _cluster.ActorProvider.GetActor<IHeartbeatActor>(NewActorKey());
        var config = new HeartbeatConfig { Enabled = false, Cron = "*/30 * * * *" };

        await actor.StartAsync(config);
        var state = await actor.GetStateAsync();

        state.IsRunning.ShouldBeFalse();
        state.NextRun.ShouldBeNull();
    }

    [Fact]
    public async Task StartAsync_CalledTwice_SecondCallIsNoOp()
    {
        var actor = _cluster.ActorProvider.GetActor<IHeartbeatActor>(NewActorKey());
        var config = new HeartbeatConfig { Enabled = true, Cron = "*/1440 * * * *" };

        await actor.StartAsync(config);
        var firstNextRun = (await actor.GetStateAsync()).NextRun;
        await actor.StartAsync(config with { Cron = "*/60 * * * *" });
        var secondNextRun = (await actor.GetStateAsync()).NextRun;

        secondNextRun.ShouldBe(firstNextRun);
    }

    [Fact]
    public async Task StopAsync_AfterStart_ClearsRunningAndNextRun()
    {
        var actor = _cluster.ActorProvider.GetActor<IHeartbeatActor>(NewActorKey());
        await actor.StartAsync(new HeartbeatConfig { Enabled = true, Cron = "*/1440 * * * *" });

        await actor.StopAsync();
        var state = await actor.GetStateAsync();

        state.IsRunning.ShouldBeFalse();
        state.NextRun.ShouldBeNull();
    }

    [Fact]
    public async Task StopAsync_WithoutStart_IsSafe()
    {
        var actor = _cluster.ActorProvider.GetActor<IHeartbeatActor>(NewActorKey());

        await actor.StopAsync();
        var state = await actor.GetStateAsync();

        state.IsRunning.ShouldBeFalse();
    }
}
