using System.Text.Json.Serialization;

namespace Weave.Agents.Pipeline.Providers.Anthropic;

internal sealed record AnthropicMessagesRequest
{
    public required string Model { get; init; }
    public required int MaxTokens { get; init; }
    public string? System { get; init; }
    public required IReadOnlyList<AnthropicMessage> Messages { get; init; }
    public bool? Stream { get; init; }
}

internal sealed record AnthropicMessage
{
    public required string Role { get; init; }
    public required string Content { get; init; }
}

internal sealed record AnthropicMessagesResponse
{
    public string? Id { get; init; }
    public string? Role { get; init; }
    public IReadOnlyList<AnthropicContentBlock>? Content { get; init; }
    public string? Model { get; init; }
    public string? StopReason { get; init; }
    public AnthropicUsage? Usage { get; init; }
}

internal sealed record AnthropicContentBlock
{
    public required string Type { get; init; }
    public string? Text { get; init; }
}

internal sealed record AnthropicUsage
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
}

internal sealed record AnthropicStreamMessageStart
{
    public AnthropicStreamMessage? Message { get; init; }
}

internal sealed record AnthropicStreamMessage
{
    public string? Id { get; init; }
    public string? Model { get; init; }
    public AnthropicUsage? Usage { get; init; }
}

internal sealed record AnthropicStreamContentBlockDelta
{
    public int Index { get; init; }
    public AnthropicStreamTextDelta? Delta { get; init; }
}

internal sealed record AnthropicStreamTextDelta
{
    public string? Type { get; init; }
    public string? Text { get; init; }
}

internal sealed record AnthropicStreamMessageDelta
{
    public AnthropicStreamStopFields? Delta { get; init; }
    public AnthropicUsage? Usage { get; init; }
}

internal sealed record AnthropicStreamStopFields
{
    public string? StopReason { get; init; }
}

[JsonSerializable(typeof(AnthropicMessagesRequest))]
[JsonSerializable(typeof(AnthropicMessagesResponse))]
[JsonSerializable(typeof(AnthropicStreamMessageStart))]
[JsonSerializable(typeof(AnthropicStreamContentBlockDelta))]
[JsonSerializable(typeof(AnthropicStreamMessageDelta))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class AnthropicMessagesJsonContext : JsonSerializerContext;
