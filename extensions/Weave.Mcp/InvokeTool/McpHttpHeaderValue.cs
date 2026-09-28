using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weave.Tools.Connectors;

internal static class McpHttpHeaderValue
{
    private const long MaxSafeInteger = 9007199254740991;

    public static string Encode(string value)
    {
        var safe = value.Length == 0 || (value[0] != ' ' && value[^1] != ' '
            && value[0] != '\t' && value[^1] != '\t'
            && value.All(static ch => ch is >= ' ' and <= '~' && ch != '\u007f')
            && !(value.StartsWith("=?base64?", StringComparison.Ordinal)
                && value.EndsWith("?=", StringComparison.Ordinal)));
        return safe ? value : $"=?base64?{Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}?=";
    }

    public static IReadOnlyDictionary<string, string> ForTool(McpTool tool, JsonNode arguments)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (tool.InputSchema is not { ValueKind: JsonValueKind.Object } schema
            || !schema.TryGetProperty("type", out var rootType)
            || rootType.ValueKind != JsonValueKind.String || rootType.GetString() != "object")
            throw new InvalidOperationException("MCP tool input schema must be an object.");
        Collect(schema, arguments, headers, new HashSet<string>(StringComparer.OrdinalIgnoreCase), isProperty: false);
        return headers;
    }

    public static bool HasValidSchema(McpTool tool)
    {
        try
        {
            ForTool(tool, new JsonObject());
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void Collect(JsonElement schema, JsonNode? value, Dictionary<string, string> headers,
        HashSet<string> names, bool isProperty)
    {
        if (schema.TryGetProperty("x-mcp-header", out var annotation))
        {
            if (!isProperty || annotation.ValueKind != JsonValueKind.String
                || annotation.GetString() is not { } name || !ValidName(name)
                || !schema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || type.GetString() is not ("string" or "integer" or "boolean"))
                throw new InvalidOperationException("MCP tool has an invalid HTTP header annotation.");
            if (!names.Add(name))
                throw new InvalidOperationException("MCP tool has duplicate HTTP header annotations.");
            if (value is not null)
                headers[name] = ConvertValue(value, type.GetString()!);
        }

        foreach (var field in schema.EnumerateObject())
        {
            if (field.NameEquals("properties") && field.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in field.Value.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Object)
                    {
                        if (ContainsAnnotation(property.Value))
                            throw new InvalidOperationException("MCP tool has an unreachable HTTP header annotation.");
                        continue;
                    }
                    var child = value is JsonObject instance ? instance[property.Name] : null;
                    Collect(property.Value, child, headers, names, isProperty: true);
                }
            }
            else if (ContainsAnnotation(field.Value))
                throw new InvalidOperationException("MCP tool has an unreachable HTTP header annotation.");
        }
    }

    private static bool ContainsAnnotation(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
            return element.EnumerateObject().Any(property => property.NameEquals("x-mcp-header")
                || ContainsAnnotation(property.Value));
        return element.ValueKind == JsonValueKind.Array && element.EnumerateArray().Any(ContainsAnnotation);
    }

    private static bool ValidName(string name) => name.Length > 0 && name.All(static ch =>
        ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~');

    private static string ConvertValue(JsonNode value, string type)
    {
        try
        {
            return type switch
            {
                "string" => value.GetValue<string>(),
                "boolean" => value.GetValue<bool>() ? "true" : "false",
                "integer" when long.TryParse(value.ToJsonString(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var number) && number is >= -MaxSafeInteger and <= MaxSafeInteger
                    => number.ToString(CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException("MCP header argument does not match its schema type.")
            };
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException("MCP header argument does not match its schema type.");
        }
    }
}
