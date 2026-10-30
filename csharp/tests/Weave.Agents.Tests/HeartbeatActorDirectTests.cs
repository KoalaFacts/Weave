using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Heartbeat;
using HeartbeatConfig = Weave.Agents.Heartbeat.HeartbeatConfig;

namespace Weave.Agents.Tests;

/// <summary>
/// Tests that instantiate <see cref="HeartbeatActor"/> directly, outside
/// an Orleans cluster. Covers <c>GetStateAsync</c>, <c>StopAsync</c>,
/// <c>Dispose</c>, and the <c>GetPrimaryKeyString</c> NRE fallback —
/// surfaces that don't require the Orleans timer API infrastructure.
/// </summary>
public sealed class HeartbeatActorDirectTests
{
    private static HeartbeatActor CreateActor() => new(
        Substitute.For<IVirtualActorProvider>(),
        Substitute.For<IActorTimerRegistry>(),
        TimeProvider.System,
        NullLogger<HeartbeatActor>.Instance);

    [Fact]
    public async Task GetStateAsync_InitialState_IsNotRunningWithNoLastRun()
    {
        var actor = CreateActor();

        var state = await actor.GetStateAsync();

        state.IsRunning.ShouldBeFalse();
        state.LastRun.ShouldBeNull();
        state.NextRun.ShouldBeNull();
        state.ExecutionCount.ShouldBe(0);
    }

    [Fact]
    public async Task StartAsync_WithDisabledConfig_DoesNotMarkRunning()
    {
        var actor = CreateActor();
        var config = new HeartbeatConfig { Enabled = false, Cron = "*/5 * * * *" };

        await actor.StartAsync(config);

        var state = await actor.GetStateAsync();
        state.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task StopAsync_WhenNotRunning_IsSafeAndStateRemainsStopped()
    {
        var actor = CreateActor();

        await actor.StopAsync();

        var state = await actor.GetStateAsync();
        state.IsRunning.ShouldBeFalse();
        state.NextRun.ShouldBeNull();
    }

    [Fact]
    public void Dispose_WithNoTimer_DoesNotThrow()
    {
        var actor = CreateActor();

        Should.NotThrow(actor.Dispose);
    }
}
