using System.Text.Json;

using Weave.Tools.Models;

namespace Weave.Tools.Builders;

public static class ToolInvocationBuilder
{
    public static ToolInvocation FromInput(string toolName, string? input)
    {
        if (!string.IsNullOrEmpty(input) && input.AsSpan().TrimStart().StartsWith("{".AsSpan(), StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(input);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var method = "invoke";
                    string? rawInput = null;
                    var parameters = new Dictionary<string, string>(StringComparer.Ordinal);

                    foreach (var property in doc.RootElement.EnumerateObject())
                    {
                        if (property.Name == "method")
                        {
                            method = property.Value.GetString() ?? "invoke";
                        }
                        else if (property.Name == "rawInput")
                        {
                            rawInput = property.Value.GetString();
                        }
                        else
                        {
                            parameters[property.Name] = property.Value.ValueKind == JsonValueKind.String
                                ? property.Value.GetString() ?? string.Empty
                                : property.Value.GetRawText();
                        }
                    }

                    return new ToolInvocation
                    {
                        ToolName = toolName,
                        Method = method,
                        RawInput = rawInput,
                        Parameters = parameters
                    };
                }
            }
            catch (JsonException)
            {
                // Fall through to fallback
            }
        }

        return new ToolInvocation
        {
            ToolName = toolName,
            Method = "invoke",
            RawInput = input,
            Parameters = []
        };
    }

    public static string DescribeSchema(ToolSchema schema)
    {
        if (schema.Parameters.Count == 0)
        {
            return schema.Description;
        }

        var paramDescriptions = schema.Parameters.Select(p =>
        {
            var typeLabel = p.Required ? $"{p.Type}, required" : p.Type;
            return $"{p.Name} ({typeLabel}): {p.Description}";
        });

        return $"{schema.Description} Parameters: {string.Join("; ", paramDescriptions)}. Pass a JSON object if multiple fields are required.";
    }
}
