using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Weave.Agents.Pipeline;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from fallback chat client.")]
internal sealed class FallbackToolCallParser
{
    public bool TryCreateFunctionCall(string userText, ChatOptions? options, out ChatMessage message)
    {
        const string Prefix = "/tool ";
        if (!userText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            message = default!;
            return false;
        }

        var payload = userText[Prefix.Length..].Trim();
        var firstSpace = payload.IndexOf(' ');
        var toolName = firstSpace >= 0 ? payload[..firstSpace] : payload;
        var input = firstSpace >= 0 ? payload[(firstSpace + 1)..].Trim() : string.Empty;

        if (options?.Tools is not { Count: > 0 } tools || !tools.Any(t => string.Equals(t.Name, toolName, StringComparison.Ordinal)))
        {
            message = new ChatMessage(ChatRole.Assistant, $"Tool '{toolName}' is not available.");
            return true;
        }

        Dictionary<string, object?> arguments = new(StringComparer.Ordinal)
        {
            ["input"] = input
        };

        if (TryParseStructuredToolInput(input, out var method, out var structuredArguments))
        {
            arguments = structuredArguments.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.Ordinal);
            arguments["input"] = input;
            arguments["method"] = method;
        }

        message = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), toolName, arguments)]);
        return true;
    }

    private static bool TryParseStructuredToolInput(
        string input,
        out string method,
        out Dictionary<string, object> arguments)
    {
        method = "invoke";
        arguments = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["method"] = method,
            ["input"] = input
        };

        if (string.IsNullOrWhiteSpace(input) || !input.TrimStart().StartsWith('{'))
            return false;

        try
        {
            using var document = JsonDocument.Parse(input);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
                return false;

            arguments = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                arguments[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number when property.Value.TryGetInt64(out var value) => value,
                    JsonValueKind.Number => property.Value.GetDouble(),
                    JsonValueKind.Object or JsonValueKind.Array => property.Value.Clone(),
                    JsonValueKind.Null or JsonValueKind.Undefined => null!,
                    _ => property.Value.Clone()
                };
            }

            if (arguments.TryGetValue("method", out var methodValue) && methodValue is not null)
                method = methodValue.ToString() ?? "invoke";

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
