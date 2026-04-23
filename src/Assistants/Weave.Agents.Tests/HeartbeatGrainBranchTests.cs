using Microsoft.Extensions.Logging;
using Weave.Agents.Heartbeat;

namespace Weave.Agents.Tests;

/// <summary>
/// Branch tests that don't require an Orleans runtime. The timer
/// registration in <see cref="HeartbeatGrain.StartAsync"/> uses
/// <c>this.RegisterGrainTimer</c> which needs a live grain context,
/// so "enabled" paths aren't reachable from direct instantiation —
/// but the early-return branches (disabled config, already running)
/// and the state accessors all are.
/// </summary>
public sealed class HeartbeatGrainBranchTests
{
    private static HeartbeatGrain CreateGrain()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var logger = Substitute.For<ILogger<HeartbeatGrain>>();
        return new HeartbeatGrain(grainFactory, TimeProvider.System, logger);
    }

    public sealed class StartAsync
    {
        [Fact]
        public async Task With_disabled_config_returns_early_without_running()
        {
            using var grain = CreateGrain();
            var config = new HeartbeatConfig { Enabled = false, Cron = "*/30 * * * *" };

            await grain.StartAsync(config);

            var state = await grain.GetStateAsync();
            state.IsRunning.ShouldBeFalse();
            state.NextRun.ShouldBeNull();
        }
    }

    public sealed class StopAsync
    {
        [Fact]
        public async Task Without_prior_start_is_idempotent()
        {
            using var grain = CreateGrain();

            await grain.StopAsync();

            var state = await grain.GetStateAsync();
            state.IsRunning.ShouldBeFalse();
            state.NextRun.ShouldBeNull();
        }
    }

    public sealed class GetStateAsync
    {
        [Fact]
        public async Task On_fresh_grain_returns_default_state()
        {
            using var grain = CreateGrain();

            var state = await grain.GetStateAsync();

            state.IsRunning.ShouldBeFalse();
            state.ExecutionCount.ShouldBe(0);
            state.LastRun.ShouldBeNull();
            state.NextRun.ShouldBeNull();
        }
    }

    public sealed class Dispose
    {
        [Fact]
        public void Dispose_on_fresh_grain_does_not_throw()
        {
            var grain = CreateGrain();

            Should.NotThrow(() => grain.Dispose());
        }

        [Fact]
        public void Dispose_twice_does_not_throw()
        {
            var grain = CreateGrain();
            grain.Dispose();

            Should.NotThrow(() => grain.Dispose());
        }
    }
}
