using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Weave.Agents.Models;

namespace Weave.Agents.Pipeline;

public static class ChatMessageMapper
{
    private static readonly JsonSerializerOptions ToolInputJsonOptions = new(JsonSerializerDefaults.Web);

    public static ChatMessage ToChatMessage(ConversationMessage historyMessage)
    {
        var role = historyMessage.Role.ToLowerInvariant() switch
        {
            "assistant" => ChatRole.Assistant,
            "system" => ChatRole.System,
            "tool" => ChatRole.Tool,
            _ => ChatRole.User
        };

        return new ChatMessage(role, historyMessage.Content)
        {
            CreatedAt = historyMessage.Timestamp
        };
    }

    [UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode", Justification = "Function call arguments are dynamic LLM outputs serialized for diagnostic logging only.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Function call arguments are dynamic LLM outputs serialized for diagnostic logging only.")]
    public static IEnumerable<ConversationMessage> ToConversationMessages(ChatMessage message, TimeProvider timeProvider)
    {
        var fallback = message.CreatedAt ?? timeProvider.GetUtcNow();

        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            yield return new ConversationMessage
            {
                Role = message.Role.Value,
                Content = message.Text,
                Timestamp = fallback
            };
        }

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case FunctionCallContent functionCall:
                    yield return new ConversationMessage
                    {
                        Role = "tool",
                        Content = $"Requested tool '{functionCall.Name}' with arguments: {JsonSerializer.Serialize(functionCall.Arguments, ToolInputJsonOptions)}",
                        Timestamp = fallback
                    };
                    break;
                case FunctionResultContent functionResult:
                    yield return new ConversationMessage
                    {
                        Role = "tool",
                        Content = $"Tool result: {functionResult.Result}",
                        Timestamp = fallback
                    };
                    break;
            }
        }
    }
}
