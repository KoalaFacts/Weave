using Orleans;
using Weave.Shared.VirtualActors;

namespace Weave.Silo.VirtualActors;

/// <summary>
/// Adapts Orleans grain timer registration to the domain
/// <see cref="IActorTimerRegistry"/> abstraction.
/// </summary>
public sealed class OrleansActorTimerRegistry(Grain grain) : IActorTimerRegistry
{
    public IDisposable RegisterTimer(Func<CancellationToken, Task> callback, TimeSpan dueTime, TimeSpan period)
        => grain.RegisterGrainTimer(callback, dueTime, period);
}
