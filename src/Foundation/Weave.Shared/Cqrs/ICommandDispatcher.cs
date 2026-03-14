namespace Weave.Shared.Cqrs;

public interface ICommandDispatcher
{
    Task<TResult> DispatchAsync<TCommand, TResult>(TCommand command, CancellationToken ct);
}
