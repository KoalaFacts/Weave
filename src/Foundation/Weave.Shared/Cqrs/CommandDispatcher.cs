using Microsoft.Extensions.DependencyInjection;

namespace Weave.Shared.Cqrs;

public sealed class CommandDispatcher(IServiceProvider serviceProvider) : ICommandDispatcher
{
    public Task<TResult> DispatchAsync<TCommand, TResult>(TCommand command, CancellationToken ct)
    {
        var handler = serviceProvider.GetRequiredService<ICommandHandler<TCommand, TResult>>();
        return handler.HandleAsync(command, ct);
    }
}
