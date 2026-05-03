namespace Weave.Dashboard.Api;

public sealed record ConversationMessageDto
{
    public string Role { get; init; } = "";
    public string Content { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }
}
