namespace Weave.Dashboard.Services;

public sealed record ConversationMessageDto
{
    public string Role { get; init; } = "";
    public string Content { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }
}
