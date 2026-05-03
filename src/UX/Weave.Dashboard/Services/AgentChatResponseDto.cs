namespace Weave.Dashboard.Services;

public sealed record AgentChatResponseDto
{
    public string Content { get; init; } = "";
    public string ConversationId { get; init; } = "";
    public List<ConversationMessageDto> Messages { get; init; } = [];
    public bool UsedTools { get; init; }
    public string? Model { get; init; }
}
