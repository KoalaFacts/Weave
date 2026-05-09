using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace Weave.Agents.Pipeline.Providers;

/// <summary>Default <see cref="IProviderResolver"/>; dispatches by <c>modelId</c> prefix.</summary>
/// <remarks>
/// <para>OpenAI models (<c>gpt-*</c>, <c>o1-*</c>, <c>o3-*</c>, <c>o4-*</c>) route
/// through <c>Microsoft.Extensions.AI.OpenAI</c> when <c>OPENAI_API_KEY</c> is set.
/// Every other input — unknown prefix or missing credential — falls back to the
/// in-process echo client so tests stay deterministic and the silo doesn't crash
/// on startup.</para>
/// <para>Anthropic / Claude models do not yet have a Microsoft first-party adapter.
/// Until one ships (or a hand-rolled <see cref="IChatClient"/> lands), <c>claude-*</c>
/// requests hit the fallback. Tracked as P0.1 step 3 in the LLM-providers plan.</para>
/// </remarks>
public sealed class ProviderResolver(
    IAgentCredentialStore credentials,
    ILoggerFactory loggerFactory) : IProviderResolver
{
    public IChatClient Resolve(string agentId, string? modelId)
    {
        if (modelId is not null && IsOpenAiModel(modelId))
        {
            var apiKey = credentials.GetApiKey("openai");
            if (apiKey is not null)
                return new ChatClient(modelId, apiKey).AsIChatClient();
        }

        return new FallbackChatClient(modelId, loggerFactory.CreateLogger<FallbackChatClient>());
    }

    private static bool IsOpenAiModel(string modelId) =>
        modelId.StartsWith("gpt-", StringComparison.Ordinal)
        || modelId.StartsWith("o1-", StringComparison.Ordinal)
        || modelId.StartsWith("o3-", StringComparison.Ordinal)
        || modelId.StartsWith("o4-", StringComparison.Ordinal);
}
