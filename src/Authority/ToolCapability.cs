namespace Weave.Authority;

/// <summary>Canonical operation identities and attenuation for the current workspace-scoped tool registry.</summary>
public static class ToolCapability
{
    private const int MaximumComponentLength = 400;

    public static string Connect(string toolName) => $"tool:{Encode(toolName)}:connect";

    public static string AllInvocations(string toolName) => $"tool:{Encode(toolName)}:invoke:*";

    public static string Invoke(string toolName, string operation) => $"tool:{Encode(toolName)}:invoke:{Encode(operation)}";

    /// <summary>Restricts covering grants to invocation on one tool, never connection or secret access.</summary>
    public static IReadOnlyList<string> ConstrainInvocations(IEnumerable<string> capabilities, string toolName)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var tool = Encode(toolName);
        var prefix = $"tool:{tool}:invoke:";
        var result = new HashSet<string>(StringComparer.Ordinal);
        string[] expected = ["tool", tool, "invoke"];

        foreach (var capability in capabilities)
        {
            if (string.IsNullOrWhiteSpace(capability) || capability.Length > 1024)
                continue;

            var segments = capability.Split(':');
            for (var i = 0; i < segments.Length && i < 4; i++)
            {
                var segment = segments[i];
                if (segment == "*" && i == segments.Length - 1)
                {
                    result.Add(prefix + "*");
                    break;
                }
                if (i < 3)
                {
                    if (segment != "*" && !string.Equals(segment, expected[i], StringComparison.Ordinal))
                        break;
                }
                else if (segments.Length == 4 && IsCanonicalComponent(segment))
                {
                    result.Add(prefix + segment);
                }
            }
        }

        return [.. result.Order(StringComparer.Ordinal)];
    }

    private static bool IsCanonicalComponent(string value)
    {
        if (value.Length is 0 or > MaximumComponentLength || value == "*")
            return false;
        try
        {
            return string.Equals(Encode(Uri.UnescapeDataString(value)), value, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Encode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaximumComponentLength || value.Any(char.IsControl))
            throw new ArgumentException("Tool and operation names must be bounded and contain no control characters.", nameof(value));

        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i]))
                continue;
            if (!char.IsHighSurrogate(value[i]) || i + 1 == value.Length || !char.IsLowSurrogate(value[++i]))
                throw new ArgumentException("Tool and operation names must contain valid Unicode.", nameof(value));
        }

        var encoded = Uri.EscapeDataString(value);
        if (encoded.Length > MaximumComponentLength)
            throw new ArgumentException("Encoded tool or operation name exceeds its size limit.", nameof(value));
        return encoded;
    }
}
