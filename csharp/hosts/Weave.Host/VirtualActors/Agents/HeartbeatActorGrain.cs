using Weave.Agents.Heartbeat;

namespace Weave.Silo.VirtualActors;

#pragma warning disable CA1001 // Disposal handled in OnDeactivateAsync
public sealed class HeartbeatActorGrain : Grain, IHeartbeatActorGrain
#pragma warning restore CA1001
{
    private readonly HeartbeatActor _actor;

    public HeartbeatActorGrain(
        IVirtualActorProvider actors,
        TimeProvider timeProvider,
        ILogger<HeartbeatActor> logger)
    {
        _actor = new HeartbeatActor(actors, new OrleansActorTimerRegistry(this), timeProvider, logger);
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        _actor.OnActivatedAsync(this.GetPrimaryKeyString(), cancellationToken);

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _actor.Dispose();
        return base.OnDeactivateAsync(reason, cancellationToken);
    }

    public Task StartAsync(HeartbeatConfig config) => _actor.StartAsync(config);
    public Task StopAsync() => _actor.StopAsync();
    public Task<HeartbeatState> GetStateAsync() => _actor.GetStateAsync();
}
