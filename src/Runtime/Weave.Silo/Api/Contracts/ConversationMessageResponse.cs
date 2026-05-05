using Weave.Agents.Chat;
namespace Weave.Silo.Api;

public sealed record ConversationMessageResponse
{
    public required string Role { get; init; }
    public required string Content { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    public static ConversationMessageResponse FromMessage(ConversationMessage message) => new()
    {
        Role = message.Role,
        Content = message.Content,
        Timestamp = message.Timestamp
    };
}
