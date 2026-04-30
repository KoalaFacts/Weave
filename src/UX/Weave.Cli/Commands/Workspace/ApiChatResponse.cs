namespace Weave.Cli.Commands;

internal sealed record ApiChatResponse
{
    public required string Content { get; init; }
    public required string ConversationId { get; init; }
    public required bool UsedTools { get; init; }
    public List<ApiConversationMessage> Messages { get; init; } = [];
    public string? Model { get; init; }
}
