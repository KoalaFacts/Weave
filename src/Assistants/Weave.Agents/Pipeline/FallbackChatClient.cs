using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Weave.Agents.Pipeline;

internal sealed partial class FallbackChatClient(
    string? defaultModelId,
    ILogger<FallbackChatClient> logger,
    FallbackToolCallParser? toolCallParser = null) : IChatClient
{
    private readonly ChatClientMetadata _metadata = new("weave-fallback", new Uri("https://weave.local/"), defaultModelId ?? "weave-local");
    private readonly FallbackToolCallParser _toolCallParser = toolCallParser ?? new FallbackToolCallParser();

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        var lastFunctionResult = FindLastFunctionResult(messageList);
        ChatResponse response;

        if (lastFunctionResult is not null)
        {
            response = new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                $"Tool result: {lastFunctionResult.Result}"))
            {
                ModelId = options?.ModelId ?? _metadata.DefaultModelId,
                Usage = CreateUsage(messageList, $"Tool result: {lastFunctionResult.Result}")
            };
        }
        else
        {
            var userText = messageList.LastOrDefault(static m => m.Role == ChatRole.User)?.Text ?? string.Empty;
            if (_toolCallParser.TryCreateFunctionCall(userText, options, out var functionMessage))
            {
                response = new ChatResponse(functionMessage)
                {
                    ModelId = options?.ModelId ?? _metadata.DefaultModelId,
                    Usage = CreateUsage(messageList, userText)
                };
            }
            else
            {
                var text = string.IsNullOrWhiteSpace(userText)
                    ? "No input provided."
                    : $"[{options?.ModelId ?? _metadata.DefaultModelId}] {userText}";
                response = new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
                {
                    ModelId = options?.ModelId ?? _metadata.DefaultModelId,
                    Usage = CreateUsage(messageList, text)
                };
            }
        }

        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        LogStreamingNotSupported(response.ModelId);
        yield break;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? _metadata : null;

    public void Dispose() { }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fallback chat client does not support incremental streaming; returning no updates for {ModelId}")]
    private partial void LogStreamingNotSupported(string? modelId);

    private static FunctionResultContent? FindLastFunctionResult(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages.Reverse())
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionResultContent result)
                    return result;
            }
        }

        return null;
    }

    private static UsageDetails CreateUsage(IEnumerable<ChatMessage> messages, string outputText)
    {
        var inputTokens = 0;
        foreach (var message in messages)
        {
            inputTokens += CountPseudoTokens(message.Text);
        }

        return new UsageDetails
        {
            InputTokenCount = inputTokens,
            OutputTokenCount = CountPseudoTokens(outputText)
        };
    }

    private static int CountPseudoTokens(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }
}
