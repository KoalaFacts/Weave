using System.Text.Json;
using Microsoft.Extensions.AI;
using Weave.Agents.Chat;
namespace Weave.Agents.Pipeline;

public static class ChatMessageMapper
{
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
                        Content = $"Requested tool '{functionCall.Name}' with arguments: {FormatToolArguments(functionCall.Arguments ?? new Dictionary<string, object?>())}",
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

    private static string FormatToolArguments(IDictionary<string, object?> arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in arguments)
            {
                writer.WritePropertyName(name);
                WriteJsonValue(writer, value);
            }
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}
