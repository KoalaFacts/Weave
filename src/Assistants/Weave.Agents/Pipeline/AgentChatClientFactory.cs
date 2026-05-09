using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weave.Agents.Pipeline.Providers;
using Weave.Workspaces.Manifest;

namespace Weave.Agents.Pipeline;

/// <summary>
/// Scoped factory — one instance per actor activation or HTTP request.
/// </summary>
/// <remarks>
/// The injected <see cref="IServiceProvider"/> is the consumer's own scope,
/// so <see cref="ChatClientBuilder"/> middleware can resolve any scoped
/// dependencies it pulls in. No <c>IServiceScopeFactory</c>: reaching for
/// one would mean the factory was registered with the wrong lifetime.
/// See docs/best-practices.md — "Prefer Scoped over Singleton+ScopeFactory".
/// Provider selection is delegated to <see cref="IProviderResolver"/>; the
/// rate-limiting / cost-tracking / function-invocation wrappers stay
/// provider-agnostic.
/// </remarks>
public sealed class AgentChatClientFactory(
    IServiceProvider services,
    IAgentCostLedger costLedger,
    IProviderResolver providerResolver,
    ILoggerFactory loggerFactory) : IAgentChatClientFactory
{
    public async Task<IChatClient> CreateAsync(
        string agentId,
        AgentDefinition? definition,
        CancellationToken ct = default)
    {
        var baseClient = await providerResolver.ResolveAsync(agentId, definition, ct).ConfigureAwait(false);
        var rateLimited = new RateLimitingChatClient(
            baseClient,
            maxRequestsPerMinute: 60,
            loggerFactory.CreateLogger<RateLimitingChatClient>());
        var tracked = new CostTrackingChatClient(
            rateLimited,
            costLedger,
            loggerFactory.CreateLogger<CostTrackingChatClient>());

        var builder = new ChatClientBuilder(tracked)
            .UseFunctionInvocation(loggerFactory);

        return builder.Build(services);
    }
}
