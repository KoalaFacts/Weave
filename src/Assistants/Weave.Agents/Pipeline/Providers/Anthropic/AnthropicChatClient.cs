using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Weave.Agents.Pipeline.Providers.Anthropic;

/// <summary>Hand-rolled <see cref="IChatClient"/> for Anthropic's Messages API.</summary>
/// <remarks>
/// Microsoft does not ship a first-party Anthropic adapter at any layer
/// (<c>Microsoft.Extensions.AI.*</c> / <c>Microsoft.Agents.AI.*</c> /
/// <c>SemanticKernel.Connectors.*</c>). This thin client posts JSON to
/// <c>https://api.anthropic.com/v1/messages</c> and translates the response
/// to <see cref="ChatResponse"/>, keeping the abstractions on Microsoft's
/// surface and the wire shape under our own control.
///
/// <para>First cut: text-only multi-turn chat. Tool use, image content,
/// and incremental streaming are deferred until a real consumer needs them
/// — streaming today returns a single <see cref="ChatResponseUpdate"/>
/// covering the full response.</para>
/// </remarks>
internal sealed class AnthropicChatClient : IChatClient
{
    private const string DefaultBaseUrl = "https://api.anthropic.com";
    private const string MessagesPath = "/v1/messages";
    private const string AnthropicApiVersion = "2023-06-01";
    private const int DefaultMaxTokens = 4096;

    private readonly HttpClient _httpClient;
    private readonly string _modelId;
    private readonly string _apiKey;
    private readonly string _messagesEndpoint;
    private readonly ChatClientMetadata _metadata;

    public AnthropicChatClient(HttpClient httpClient, string modelId, string apiKey, string? baseUrl = null)
    {
        _httpClient = httpClient;
        _modelId = modelId;
        _apiKey = apiKey;
        var origin = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _messagesEndpoint = origin + MessagesPath;
        _metadata = new ChatClientMetadata("anthropic", new Uri(origin + "/"), modelId);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var (system, conversation) = SplitSystemAndConversation(messages);
        var requestBody = new AnthropicMessagesRequest
        {
            Model = options?.ModelId ?? _modelId,
            MaxTokens = options?.MaxOutputTokens ?? DefaultMaxTokens,
            System = system,
            Messages = conversation
        };

        using var httpRequest = BuildRequest(requestBody);
        using var httpResponse = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        httpResponse.EnsureSuccessStatusCode();

        await using var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var parsed = await JsonSerializer.DeserializeAsync(
                stream,
                AnthropicMessagesJsonContext.Default.AnthropicMessagesResponse,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Anthropic response body was empty.");

        return BuildChatResponse(parsed);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        var text = response.Text;
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent(text)],
            ModelId = response.ModelId,
            ResponseId = response.ResponseId
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? _metadata : null;

    public void Dispose()
    {
    }

    private HttpRequestMessage BuildRequest(AnthropicMessagesRequest body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            body,
            AnthropicMessagesJsonContext.Default.AnthropicMessagesRequest);

        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, _messagesEndpoint)
        {
            Content = content
        };
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", AnthropicApiVersion);
        return request;
    }

    private static (string? System, IReadOnlyList<AnthropicMessage> Conversation) SplitSystemAndConversation(
        IEnumerable<ChatMessage> messages)
    {
        var systemBuilder = new StringBuilder();
        var conversation = new List<AnthropicMessage>();
        foreach (var message in messages)
        {
            var text = message.Text;
            if (string.IsNullOrEmpty(text))
                continue;

            if (message.Role == ChatRole.System)
            {
                if (systemBuilder.Length > 0)
                    systemBuilder.Append('\n');
                systemBuilder.Append(text);
            }
            else
            {
                var role = message.Role == ChatRole.Assistant ? "assistant" : "user";
                conversation.Add(new AnthropicMessage { Role = role, Content = text });
            }
        }
        var system = systemBuilder.Length == 0 ? null : systemBuilder.ToString();
        return (system, conversation);
    }

    private ChatResponse BuildChatResponse(AnthropicMessagesResponse parsed)
    {
        var text = ExtractText(parsed.Content);
        var assistant = new ChatMessage(ChatRole.Assistant, text);
        return new ChatResponse(assistant)
        {
            ResponseId = parsed.Id,
            ModelId = parsed.Model ?? _modelId,
            FinishReason = MapFinishReason(parsed.StopReason),
            Usage = parsed.Usage is null
                ? null
                : new UsageDetails
                {
                    InputTokenCount = parsed.Usage.InputTokens,
                    OutputTokenCount = parsed.Usage.OutputTokens
                }
        };
    }

    private static string ExtractText(IReadOnlyList<AnthropicContentBlock>? blocks)
    {
        if (blocks is null || blocks.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var block in blocks)
        {
            if (block.Type == "text" && !string.IsNullOrEmpty(block.Text))
                builder.Append(block.Text);
        }
        return builder.ToString();
    }

    private static ChatFinishReason? MapFinishReason(string? stopReason) => stopReason switch
    {
        "end_turn" => ChatFinishReason.Stop,
        "max_tokens" => ChatFinishReason.Length,
        "stop_sequence" => ChatFinishReason.Stop,
        "tool_use" => ChatFinishReason.ToolCalls,
        _ => null
    };
}
