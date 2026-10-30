using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Weave.Agents.Pipeline.Providers.Anthropic;

internal static class AnthropicStreamingMapper
{
    public static async IAsyncEnumerable<ChatResponseUpdate> MapEventsAsync(
        IAsyncEnumerable<AnthropicSseEvent> events,
        string defaultModelId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? responseId = null;
        string? modelId = null;
        int? inputTokens = null;
        int? outputTokens = null;
        string? stopReason = null;

        await foreach (var sse in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            switch (sse.Event)
            {
                case "message_start":
                    var start = JsonSerializer.Deserialize(
                        sse.Data,
                        AnthropicMessagesJsonContext.Default.AnthropicStreamMessageStart);
                    if (start?.Message is { } msg)
                    {
                        responseId = msg.Id;
                        modelId = msg.Model;
                        inputTokens = msg.Usage?.InputTokens;
                    }
                    break;

                case "content_block_delta":
                    var blockDelta = JsonSerializer.Deserialize(
                        sse.Data,
                        AnthropicMessagesJsonContext.Default.AnthropicStreamContentBlockDelta);
                    if (blockDelta?.Delta is { Type: "text_delta", Text: { Length: > 0 } text })
                    {
                        yield return new ChatResponseUpdate
                        {
                            Role = ChatRole.Assistant,
                            Contents = [new TextContent(text)],
                            ModelId = modelId ?? defaultModelId,
                            ResponseId = responseId
                        };
                    }
                    break;

                case "message_delta":
                    var messageDelta = JsonSerializer.Deserialize(
                        sse.Data,
                        AnthropicMessagesJsonContext.Default.AnthropicStreamMessageDelta);
                    if (messageDelta is not null)
                    {
                        stopReason = messageDelta.Delta?.StopReason ?? stopReason;
                        outputTokens = messageDelta.Usage?.OutputTokens ?? outputTokens;
                    }
                    break;

                case "message_stop":
                    yield return BuildFinalUpdate(
                        responseId,
                        modelId ?? defaultModelId,
                        inputTokens,
                        outputTokens,
                        stopReason);
                    break;

                case "error":
                    throw new InvalidOperationException(
                        $"Anthropic stream emitted an error event: {sse.Data}");
            }
        }
    }

    private static ChatResponseUpdate BuildFinalUpdate(
        string? responseId,
        string modelId,
        int? inputTokens,
        int? outputTokens,
        string? stopReason)
    {
        var contents = new List<AIContent>();
        if (inputTokens is not null || outputTokens is not null)
        {
            contents.Add(new UsageContent(new UsageDetails
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens
            }));
        }

        return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = contents,
            ModelId = modelId,
            ResponseId = responseId,
            FinishReason = MapFinishReason(stopReason)
        };
    }

    internal static ChatFinishReason? MapFinishReason(string? stopReason) => stopReason switch
    {
        "end_turn" => ChatFinishReason.Stop,
        "max_tokens" => ChatFinishReason.Length,
        "stop_sequence" => ChatFinishReason.Stop,
        "tool_use" => ChatFinishReason.ToolCalls,
        _ => null
    };
}
