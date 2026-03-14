using Microsoft.Extensions.DependencyInjection;

namespace Weave.Shared.Cqrs;

public sealed class QueryDispatcher(IServiceProvider serviceProvider) : IQueryDispatcher
{
    public Task<TResult> DispatchAsync<TQuery, TResult>(TQuery query, CancellationToken ct)
    {
        var handler = serviceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>();
        return handler.HandleAsync(query, ct);
    }
}
