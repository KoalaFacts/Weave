namespace Weave.Shared.Cqrs;

public interface IQueryDispatcher
{
    Task<TResult> DispatchAsync<TQuery, TResult>(TQuery query, CancellationToken ct);
}
