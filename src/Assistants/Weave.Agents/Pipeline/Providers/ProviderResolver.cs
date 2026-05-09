using System.ClientModel;
using System.Net.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using Weave.Agents.Pipeline.Providers.Anthropic;
using Weave.Workspaces.Manifest;

namespace Weave.Agents.Pipeline.Providers;

/// <summary>Default <see cref="IProviderResolver"/>; dispatches via <see cref="AgentDefinition"/>.</summary>
/// <remarks>
/// <para>Provider selection: <c>definition.Provider</c> wins when set; otherwise the
/// model id prefix is consulted (<c>gpt-*</c>/<c>o1-*</c>/<c>o3-*</c>/<c>o4-*</c> →
/// <c>"openai"</c>; <c>claude-*</c> → <c>"anthropic"</c>).</para>
///
/// <para>API key: <c>definition.ApiKeyRef</c> (a <c>{secret:backend/path}</c>
/// placeholder) is resolved via <see cref="IAgentSecretResolver"/> when set.
/// Otherwise the global <see cref="IAgentCredentialStore"/> is consulted, which
/// reads <c>OPENAI_API_KEY</c>/<c>ANTHROPIC_API_KEY</c> from the silo's process
/// environment.</para>
///
/// <para>Endpoint override: <c>definition.BaseUrl</c> retargets the upstream client
/// (Azure OpenAI, OpenAI-compatible proxies, regional Anthropic endpoints).
/// Defaults are the public API endpoints when unset.</para>
///
/// <para>Anything missing — unknown provider, missing key, unresolvable secret —
/// falls back to <see cref="FallbackChatClient"/> so the silo doesn't crash and
/// tests stay deterministic.</para>
/// </remarks>
public sealed class ProviderResolver(
    IAgentCredentialStore credentials,
    IAgentSecretResolver secretResolver,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory) : IProviderResolver
{
    private const string AnthropicHttpClientName = "anthropic";
    private readonly ILogger<ProviderResolver> _logger = loggerFactory.CreateLogger<ProviderResolver>();

    public async Task<IChatClient> ResolveAsync(
        string agentId,
        AgentDefinition? definition,
        CancellationToken ct = default)
    {
        var modelId = definition?.Model;
        if (modelId is null)
            return Fallback(modelId);

        var explicitProvider = definition?.Provider;
        var inferredProvider = InferProvider(modelId);
        var providerName = explicitProvider ?? inferredProvider;

        if (providerName is not "openai" and not "anthropic")
            return Fallback(modelId);

        if (explicitProvider is not null
            && inferredProvider is not null
            && explicitProvider != inferredProvider)
        {
            _logger.LogWarning(
                "Agent {AgentId}: manifest provider={Explicit} disagrees with model id {ModelId} (infers {Inferred}); dispatching as configured.",
                agentId, explicitProvider, modelId, inferredProvider);
        }

        var baseUrl = definition?.BaseUrl;
        if (baseUrl is not null && !IsHttps(baseUrl))
        {
            _logger.LogWarning(
                "Agent {AgentId}: base_url={BaseUrl} is not HTTPS; the API key will travel in cleartext.",
                agentId, baseUrl);
        }

        var apiKey = await ResolveApiKeyAsync(providerName, definition?.ApiKeyRef, ct).ConfigureAwait(false);
        if (apiKey is null)
            return Fallback(modelId);

        return providerName == "openai"
            ? BuildOpenAi(modelId, apiKey, baseUrl)
            : BuildAnthropic(modelId, apiKey, baseUrl);
    }

    private Task<string?> ResolveApiKeyAsync(string providerName, string? apiKeyRef, CancellationToken ct) =>
        apiKeyRef is not null
            ? secretResolver.ResolveAsync(apiKeyRef, ct)
            : Task.FromResult(credentials.GetApiKey(providerName));

    private static IChatClient BuildOpenAi(string modelId, string apiKey, string? baseUrl)
    {
        if (baseUrl is null)
            return new ChatClient(modelId, apiKey).AsIChatClient();

        var options = new OpenAIClientOptions { Endpoint = new Uri(baseUrl) };
        return new ChatClient(modelId, new ApiKeyCredential(apiKey), options).AsIChatClient();
    }

    private AnthropicChatClient BuildAnthropic(string modelId, string apiKey, string? baseUrl) =>
        new(httpClientFactory.CreateClient(AnthropicHttpClientName),
            modelId,
            apiKey,
            baseUrl);

    private FallbackChatClient Fallback(string? modelId) =>
        new(modelId, loggerFactory.CreateLogger<FallbackChatClient>());

    private static string? InferProvider(string modelId) =>
        IsOpenAiModel(modelId) ? "openai"
        : IsAnthropicModel(modelId) ? "anthropic"
        : null;

    private static bool IsOpenAiModel(string modelId) =>
        modelId.StartsWith("gpt-", StringComparison.Ordinal)
        || modelId.StartsWith("o1-", StringComparison.Ordinal)
        || modelId.StartsWith("o3-", StringComparison.Ordinal)
        || modelId.StartsWith("o4-", StringComparison.Ordinal);

    private static bool IsAnthropicModel(string modelId) =>
        modelId.StartsWith("claude-", StringComparison.Ordinal);

    private static bool IsHttps(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps;
}
