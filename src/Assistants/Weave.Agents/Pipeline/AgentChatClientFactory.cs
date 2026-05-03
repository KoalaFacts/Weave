using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Weave.Agents.Pipeline;

/// <summary>
/// Scoped factory — one instance per actor activation or HTTP request.
/// The injected <see cref="IServiceProvider"/> is the consumer's own
/// scope, so <see cref="ChatClientBuilder"/> middleware can resolve any
/// scoped dependencies it pulls in.
///
/// No <c>IServiceScopeFactory</c>: reaching for one would mean the
/// factory was registered with the wrong lifetime. See
/// docs/best-practices.md — "Prefer Scoped over Singleton+ScopeFactory".
/// </summary>
public sealed class AgentChatClientFactory(
    IServiceProvider services,
    IAgentCostLedger costLedger,
    ILoggerFactory loggerFactory) : IAgentChatClientFactory
{
    public IChatClient Create(string agentId, string? modelId = null)
    {
        var baseClient = new FallbackChatClient(
            modelId,
            services.GetRequiredService<ILogger<FallbackChatClient>>());
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
