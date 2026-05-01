using Weave.Workspaces.Models;

namespace Weave.Tools.Connectors;

internal sealed class CliCommandPolicy
{
    private static readonly string[] ShellMetacharacters = [";", "|", "&&", "||", "`", "$(", "$((", "\n", "\r", ">>", ">&"];

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from CliToolConnector.")]
    public CliCommandPolicyResult Evaluate(string command, CliConfig config)
    {
        if (ContainsShellMetacharacters(command))
            return CliCommandPolicyResult.Blocked("Command contains prohibited shell metacharacters.");

        if (!IsCommandAllowed(command, config))
            return CliCommandPolicyResult.Blocked($"Command '{command}' is not permitted by the CLI tool policy.");

        return CliCommandPolicyResult.Allowed;
    }

    internal static bool ContainsShellMetacharacters(string command) =>
        ShellMetacharacters.Any(meta => command.Contains(meta, StringComparison.Ordinal));

    private static bool IsCommandAllowed(string command, CliConfig config)
    {
        if (config.DeniedCommands.Any(pattern => WildcardMatches(pattern, command)))
            return false;

        if (config.AllowedCommands.Count == 0)
            return true;

        return config.AllowedCommands.Any(pattern => WildcardMatches(pattern, command));
    }

    private static bool WildcardMatches(string pattern, string command)
    {
        if (pattern == "*")
            return true;

        var parts = pattern.Split('*', StringSplitOptions.None);
        var currentIndex = 0;
        var anchoredAtStart = !pattern.StartsWith('*');
        var anchoredAtEnd = !pattern.EndsWith('*');

        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length == 0)
                continue;

            var matchIndex = command.IndexOf(part, currentIndex, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
                return false;

            if (index == 0 && anchoredAtStart && matchIndex != 0)
                return false;

            currentIndex = matchIndex + part.Length;
        }

        if (!anchoredAtEnd)
            return true;

        var lastPart = parts.LastOrDefault(static p => p.Length > 0) ?? string.Empty;
        return command.EndsWith(lastPart, StringComparison.OrdinalIgnoreCase);
    }
}

internal readonly record struct CliCommandPolicyResult(bool IsAllowed, string? Error)
{
    public static CliCommandPolicyResult Allowed { get; } = new(true, null);

    public static CliCommandPolicyResult Blocked(string error) => new(false, error);
}
