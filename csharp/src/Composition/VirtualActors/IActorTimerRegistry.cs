namespace Weave.Shared.VirtualActors;

/// <summary>
/// Abstracts timer registration for virtual actors.
/// Only actors that need scheduled callbacks depend on this.
/// </summary>
public interface IActorTimerRegistry
{
    IDisposable RegisterTimer(Func<CancellationToken, Task> callback, TimeSpan dueTime, TimeSpan period);
}
