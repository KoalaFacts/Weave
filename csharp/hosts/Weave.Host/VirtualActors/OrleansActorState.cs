namespace Weave.Silo.VirtualActors;

/// <summary>
/// Adapts Orleans <see cref="IPersistentState{TState}"/> to the domain
/// <see cref="IActorState{T}"/> abstraction so that actor implementations
/// never reference Orleans directly.
/// </summary>
public sealed class OrleansActorState<T>(IPersistentState<T> inner) : IActorState<T>
    where T : new()
{
    public T State { get => inner.State; set => inner.State = value; }

    public Task ReadStateAsync(CancellationToken cancellationToken = default)
        => inner.ReadStateAsync(cancellationToken);

    public Task WriteStateAsync(CancellationToken cancellationToken = default)
        => inner.WriteStateAsync(cancellationToken);

    public Task ClearStateAsync(CancellationToken cancellationToken = default)
#pragma warning disable CA2016 // Orleans ClearStateAsync() does not accept CancellationToken
        => inner.ClearStateAsync();
#pragma warning restore CA2016
}
