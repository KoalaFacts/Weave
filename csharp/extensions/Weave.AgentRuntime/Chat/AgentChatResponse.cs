namespace Weave.Agents.Chat;

public sealed record AgentChatResponse
{
    public string Content { get; init; } = string.Empty;
    public string ConversationId { get; init; } = string.Empty;
    public List<ConversationMessage> Messages { get; init; } = [];
    public bool UsedTools { get; init; }
    public string? Model { get; init; }
}
