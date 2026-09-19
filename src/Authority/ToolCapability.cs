namespace Weave.Security.Tokens;

/// <summary>Builds unambiguous scopes and narrows grants without adding authority.</summary>
public static class ToolCapability
{
    public static string Connect(string toolName) => $"tool:{Escape(toolName)}:connect";
    public static string Invoke(string toolName, string operation) => $"{Prefix(toolName)}{Escape(operation)}";
    public static string InvokeAll(string toolName) => $"{Prefix(toolName)}*";

    public static HashSet<string> ConstrainInvocations(string toolName, IEnumerable<string> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        var prefix = Prefix(toolName);
        string[] requested = ["tool", Escape(toolName), "invoke"];
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in grants)
        {
            if (string.IsNullOrWhiteSpace(grant))
                continue;
            var segments = grant.Split(':');
            var matches = true;
            var remainderWildcard = false;
            for (var i = 0; i < requested.Length; i++)
            {
                if (i >= segments.Length)
                {
                    matches = false;
                    break;
                }
                if (segments[i] == "*" && i == segments.Length - 1)
                {
                    remainderWildcard = true;
                    break;
                }
                if (segments[i] != "*" && segments[i] != requested[i])
                {
                    matches = false;
                    break;
                }
            }
            if (!matches)
                continue;
            if (remainderWildcard)
                result.Add(prefix + "*");
            else if (segments.Length == 4 && segments[3].Length > 0)
                result.Add(prefix + segments[3]);
        }
        return result;
    }

    private static string Prefix(string toolName) => $"tool:{Escape(toolName)}:invoke:";

    private static string Escape(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Uri.EscapeDataString(value).Replace("*", "%2A", StringComparison.Ordinal);
    }
}
