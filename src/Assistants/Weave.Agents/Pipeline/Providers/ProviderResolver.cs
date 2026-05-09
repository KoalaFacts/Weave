using System.Net.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using Weave.Agents.Pipeline.Providers.Anthropic;

namespace Weave.Agents.Pipeline.Providers;

/// <summary>Default <see cref="IProviderResolver"/>; dispatches by <c>modelId</c> prefix.</summary>
/// <remarks>
/// <para>OpenAI models (<c>gpt-*</c>, <c>o1-*</c>, <c>o3-*</c>, <c>o4-*</c>) route
/// through <c>Microsoft.Extensions.AI.OpenAI</c> when <c>OPENAI_API_KEY</c> is set.</para>
/// <para>Anthropic / Claude models (<c>claude-*</c>) route through a hand-rolled
/// <see cref="AnthropicChatClient"/> hitting <c>https://api.anthropic.com/v1/messages</c>
/// directly. Microsoft does not ship a first-party Anthropic adapter; staying on
/// <c>HttpClient</c> keeps the dependency surface to <c>Microsoft.Extensions.AI</c>
/// abstractions only. <c>HttpClient</c> instances come from <see cref="IHttpClientFactory"/>
/// for connection-pooling + DelegatingHandler hooks.</para>
/// <para>Every other input — unknown prefix or missing credential — falls back to
/// the in-process echo client so tests stay deterministic and the silo doesn't
/// crash on startup.</para>
/// </remarks>
public sealed class ProviderResolver(
    IAgentCredentialStore credentials,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory) : IProviderResolver
{
    private const string AnthropicHttpClientName = "anthropic";

    public IChatClient Resolve(string agentId, string? modelId)
    {
        if (modelId is null)
            return Fallback(modelId);

        if (IsOpenAiModel(modelId))
        {
            var apiKey = credentials.GetApiKey("openai");
            if (apiKey is not null)
                return new ChatClient(modelId, apiKey).AsIChatClient();
        }
        else if (IsAnthropicModel(modelId))
        {
            var apiKey = credentials.GetApiKey("anthropic");
            if (apiKey is not null)
                return new AnthropicChatClient(
                    httpClientFactory.CreateClient(AnthropicHttpClientName),
                    modelId,
                    apiKey);
        }

        return Fallback(modelId);
    }

    private FallbackChatClient Fallback(string? modelId) =>
        new(modelId, loggerFactory.CreateLogger<FallbackChatClient>());

    private static bool IsOpenAiModel(string modelId) =>
        modelId.StartsWith("gpt-", StringComparison.Ordinal)
        || modelId.StartsWith("o1-", StringComparison.Ordinal)
        || modelId.StartsWith("o3-", StringComparison.Ordinal)
        || modelId.StartsWith("o4-", StringComparison.Ordinal);

    private static bool IsAnthropicModel(string modelId) =>
        modelId.StartsWith("claude-", StringComparison.Ordinal);
}
