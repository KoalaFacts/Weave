namespace Weave.Shared.VirtualActors;

/// <summary>
/// Abstracts persistent state for a virtual actor.
/// The infrastructure layer (e.g. Orleans, Dapr) provides the concrete implementation.
/// Domain actors depend on this interface — never on a specific runtime's state API.
/// </summary>
public interface IActorState<T> where T : new()
{
    T State { get; set; }
    Task ReadStateAsync(CancellationToken cancellationToken = default);
    Task WriteStateAsync(CancellationToken cancellationToken = default);
    Task ClearStateAsync(CancellationToken cancellationToken = default);
}
