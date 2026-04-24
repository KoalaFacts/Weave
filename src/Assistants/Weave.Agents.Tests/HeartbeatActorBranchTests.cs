using Microsoft.Extensions.Logging;
using Weave.Agents.Heartbeat;

namespace Weave.Agents.Tests;

/// <summary>
/// Branch tests that don't require an Orleans runtime. The timer
/// registration in <see cref="HeartbeatActor.StartAsync"/> uses
/// the Orleans timer API which needs a live actor context,
/// so "enabled" paths aren't reachable from direct instantiation —
/// but the early-return branches (disabled config, already running)
/// and the state accessors all are.
/// </summary>
public sealed class HeartbeatActorBranchTests
{
    private static HeartbeatActor CreateActor()
    {
        var actorFactory = Substitute.For<IActorFactory>();
        var logger = Substitute.For<ILogger<HeartbeatActor>>();
        return new HeartbeatActor(new TestVirtualActorProvider(actorFactory), TimeProvider.System, logger);
    }

    public sealed class StartAsync
    {
        [Fact]
        public async Task With_disabled_config_returns_early_without_running()
        {
            using var actor = CreateActor();
            var config = new HeartbeatConfig { Enabled = false, Cron = "*/30 * * * *" };

            await actor.StartAsync(config);

            var state = await actor.GetStateAsync();
            state.IsRunning.ShouldBeFalse();
            state.NextRun.ShouldBeNull();
        }
    }

    public sealed class StopAsync
    {
        [Fact]
        public async Task Without_prior_start_is_idempotent()
        {
            using var actor = CreateActor();

            await actor.StopAsync();

            var state = await actor.GetStateAsync();
            state.IsRunning.ShouldBeFalse();
            state.NextRun.ShouldBeNull();
        }
    }

    public sealed class GetStateAsync
    {
        [Fact]
        public async Task On_fresh_actor_returns_default_state()
        {
            using var actor = CreateActor();

            var state = await actor.GetStateAsync();

            state.IsRunning.ShouldBeFalse();
            state.ExecutionCount.ShouldBe(0);
            state.LastRun.ShouldBeNull();
            state.NextRun.ShouldBeNull();
        }
    }

    public sealed class Dispose
    {
        [Fact]
        public void Dispose_on_fresh_actor_does_not_throw()
        {
            var actor = CreateActor();

            Should.NotThrow(() => actor.Dispose());
        }

        [Fact]
        public void Dispose_twice_does_not_throw()
        {
            var actor = CreateActor();
            actor.Dispose();

            Should.NotThrow(() => actor.Dispose());
        }
    }
}
