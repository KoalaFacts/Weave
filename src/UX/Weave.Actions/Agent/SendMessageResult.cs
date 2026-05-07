namespace Weave.Actions.Agent;

/// <summary>
/// Result of <see cref="SendMessageAction"/>: the agent's reply plus the
/// conversation history snapshot the silo returned. Frontends typically
/// render <c>Content</c> as the agent reply line and replace their local
/// history with <c>Messages</c> when non-empty.
/// </summary>
public sealed record SendMessageResult
{
    public required string Content { get; init; }
    public required string ConversationId { get; init; }
    public required bool UsedTools { get; init; }
    public string? Model { get; init; }
    public IReadOnlyList<ConversationMessage> Messages { get; init; } = [];
}
