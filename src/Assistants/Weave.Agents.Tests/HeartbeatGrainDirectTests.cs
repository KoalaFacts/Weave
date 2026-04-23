using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Heartbeat;
using HeartbeatConfig = Weave.Agents.Heartbeat.HeartbeatConfig;

namespace Weave.Agents.Tests;

/// <summary>
/// Tests that instantiate <see cref="HeartbeatGrain"/> directly, outside
/// an Orleans cluster. Covers <c>GetStateAsync</c>, <c>StopAsync</c>,
/// <c>Dispose</c>, and the <c>GetPrimaryKeyString</c> NRE fallback —
/// surfaces that don't require <c>RegisterGrainTimer</c> infrastructure.
/// </summary>
public sealed class HeartbeatGrainDirectTests
{
    private static HeartbeatGrain CreateGrain() => new(
        Substitute.For<IGrainFactory>(),
        TimeProvider.System,
        NullLogger<HeartbeatGrain>.Instance);

    [Fact]
    public async Task GetStateAsync_InitialState_IsNotRunningWithNoLastRun()
    {
        var grain = CreateGrain();

        var state = await grain.GetStateAsync();

        state.IsRunning.ShouldBeFalse();
        state.LastRun.ShouldBeNull();
        state.NextRun.ShouldBeNull();
        state.ExecutionCount.ShouldBe(0);
    }

    [Fact]
    public async Task StartAsync_WithDisabledConfig_DoesNotMarkRunning()
    {
        var grain = CreateGrain();
        var config = new HeartbeatConfig { Enabled = false, Cron = "*/5 * * * *" };

        await grain.StartAsync(config);

        var state = await grain.GetStateAsync();
        state.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task StopAsync_WhenNotRunning_IsSafeAndStateRemainsStopped()
    {
        var grain = CreateGrain();

        await grain.StopAsync();

        var state = await grain.GetStateAsync();
        state.IsRunning.ShouldBeFalse();
        state.NextRun.ShouldBeNull();
    }

    [Fact]
    public void Dispose_WithNoTimer_DoesNotThrow()
    {
        var grain = CreateGrain();

        Should.NotThrow(grain.Dispose);
    }
}
