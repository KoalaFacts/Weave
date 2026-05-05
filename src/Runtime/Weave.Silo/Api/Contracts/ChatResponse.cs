using Weave.Agents.Chat;
namespace Weave.Silo.Api;

public sealed record ChatResponse
{
    public required string Content { get; init; }
    public required string ConversationId { get; init; }
    public required bool UsedTools { get; init; }
    public List<ConversationMessageResponse> Messages { get; init; } = [];
    public string? Model { get; init; }

    public static ChatResponse FromResponse(AgentChatResponse response) => new()
    {
        Content = response.Content,
        ConversationId = response.ConversationId,
        UsedTools = response.UsedTools,
        Model = response.Model,
        Messages = response.Messages.Select(ConversationMessageResponse.FromMessage).ToList()
    };
}
