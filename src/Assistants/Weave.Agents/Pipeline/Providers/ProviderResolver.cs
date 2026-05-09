using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Weave.Agents.Pipeline.Providers;

/// <summary>Default <see cref="IProviderResolver"/>; falls back to the in-process echo client.</summary>
/// <remarks>
/// The first cut returns <see cref="FallbackChatClient"/> for every request — the
/// architectural seam is in place but real provider dispatch (OpenAI, Anthropic)
/// lands in subsequent commits on this branch. The <see cref="IAgentCredentialStore"/>
/// dependency is wired up early so adding a provider doesn't change DI.
/// </remarks>
public sealed class ProviderResolver(
    IAgentCredentialStore credentials,
    ILoggerFactory loggerFactory) : IProviderResolver
{
    public IChatClient Resolve(string agentId, string? modelId)
    {
        _ = credentials;
        return new FallbackChatClient(modelId, loggerFactory.CreateLogger<FallbackChatClient>());
    }
}
