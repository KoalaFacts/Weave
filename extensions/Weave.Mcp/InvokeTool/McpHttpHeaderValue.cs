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
                        if (ContainsSchemaAnnotation(property.Value))
                            throw new InvalidOperationException("MCP tool has an unreachable HTTP header annotation.");
                        continue;
                    }
                    var child = value is JsonObject instance ? instance[property.Name] : null;
                    Collect(property.Value, child, headers, names, isProperty: true);
                }
            }
            else if (ContainsSchemaAnnotationInKeyword(field.Name, field.Value))
                throw new InvalidOperationException("MCP tool has an unreachable HTTP header annotation.");
        }
    }

    private static bool ContainsSchemaAnnotation(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.EnumerateObject().Any(property => property.NameEquals("x-mcp-header")
                || ContainsSchemaAnnotationInKeyword(property.Name, property.Value));
    }

    private static bool ContainsSchemaAnnotationInKeyword(string keyword, JsonElement value)
    {
        if (keyword is "properties" or "patternProperties" or "$defs" or "definitions" or "dependentSchemas" or "dependencies")
            return value.ValueKind == JsonValueKind.Object
                && value.EnumerateObject().Any(property => ContainsSchemaAnnotation(property.Value));
        if (keyword is "allOf" or "anyOf" or "oneOf" or "prefixItems")
            return value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(ContainsSchemaAnnotation);
        return keyword is "items" or "additionalProperties" or "unevaluatedProperties" or "unevaluatedItems"
            or "propertyNames" or "contains" or "not" or "if" or "then" or "else" or "contentSchema"
            && ContainsSchemaAnnotation(value);
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
                "integer" when TryNormalizeInteger(value, out var number) => number,
                _ => throw new InvalidOperationException("MCP header argument does not match its schema type.")
            };
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException("MCP header argument does not match its schema type.");
        }
    }

    private static bool TryNormalizeInteger(JsonNode value, out string normalized)
    {
        normalized = string.Empty;
        var raw = value.ToJsonString();
        if (value is not JsonValue)
            return false;
        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind != JsonValueKind.Number)
            return false;

        var exponentIndex = raw.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex < 0 ? raw : raw[..exponentIndex];
        var dotIndex = mantissa.IndexOf('.');
        var fractionalDigits = dotIndex < 0 ? 0 : mantissa.Length - dotIndex - 1;
        var negative = mantissa[0] == '-';
        var digits = dotIndex < 0 ? mantissa[(negative ? 1 : 0)..]
            : mantissa[(negative ? 1 : 0)..dotIndex] + mantissa[(dotIndex + 1)..];
        var significant = digits.TrimStart('0');
        if (significant.Length == 0)
        {
            normalized = "0";
            return true;
        }
        var exponent = 0;
        if (exponentIndex >= 0 && !int.TryParse(raw.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out exponent))
            return false;
        var core = significant.TrimEnd('0');
        var shift = (long)exponent - fractionalDigits + significant.Length - core.Length;
        if (shift < 0 || core.Length + shift > 16)
            return false;
        var expanded = (negative ? "-" : "") + core + new string('0', (int)shift);
        if (!long.TryParse(expanded, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)
            || integer is < -MaxSafeInteger or > MaxSafeInteger)
            return false;
        normalized = integer.ToString(CultureInfo.InvariantCulture);
        return true;
    }
}
