using Microsoft.Extensions.DependencyInjection;

namespace Weave.Shared.Cqrs;

/// <summary>
/// Scoped command dispatcher. The dispatcher is stateless, so its
/// lifetime matches the consumer's — HTTP endpoints get a request-
/// scoped dispatcher, actors get a actor-scoped one. Handlers
/// registered as <see cref="ServiceLifetime.Scoped"/> resolve
/// cleanly because <paramref name="serviceProvider"/> is the same
/// scope the dispatcher was resolved from.
///
/// We deliberately do NOT use <c>IServiceScopeFactory</c> here.
/// Reaching for a scope factory hides what a service actually
/// depends on; if you feel pulled toward it, the service is
/// probably registered with the wrong lifetime.
/// </summary>
public sealed class CommandDispatcher(IServiceProvider serviceProvider) : ICommandDispatcher
{
    public Task<TResult> DispatchAsync<TCommand, TResult>(TCommand command, CancellationToken ct)
    {
        var handler = serviceProvider.GetRequiredService<ICommandHandler<TCommand, TResult>>();
        return handler.HandleAsync(command, ct);
    }
}
