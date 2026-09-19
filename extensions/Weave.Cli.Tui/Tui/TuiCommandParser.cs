namespace Weave.Cli.Tui;

internal static class TuiCommandParser
{
    private static readonly HashSet<string> BarewordCommands =
        new(StringComparer.OrdinalIgnoreCase) { "help", "?", "quit", "exit" };

    private static readonly string[] KnownCommands =
    [
        "open", "use", "agent", "agents", "watch", "tools", "tasks",
        "history", "status", "validate", "ports", "config",
        "up", "down", "clear", "cls", "refresh", "new",
        "presets", "webui", "web", "system", "sys", "version",
        "upgrade", "update", "help", "quit", "exit"
    ];

    public static bool IsBareCommand(string name) => BarewordCommands.Contains(name);

    public static (string Name, string? Args) Parse(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
            return (string.Empty, null);

        var space = trimmed.IndexOf(' ');
        if (space < 0)
            return (trimmed.ToLowerInvariant(), null);

        var name = trimmed[..space].ToLowerInvariant();
        var args = trimmed[(space + 1)..].Trim();
        return (name, args.Length == 0 ? null : args);
    }

    public static string? Suggest(string typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
            return null;

        var prefix = KnownCommands.FirstOrDefault(
            command => command.StartsWith(typed, StringComparison.OrdinalIgnoreCase));
        if (prefix is not null)
            return prefix;

        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var command in KnownCommands)
        {
            var distance = LevenshteinDistance(command, typed);
            if (distance < bestDistance && distance <= 2)
            {
                bestDistance = distance;
                best = command;
            }
        }

        return best;
    }

    public static int LevenshteinDistance(string a, string b)
    {
        var aLength = a.Length;
        var bLength = b.Length;
        if (aLength == 0)
            return bLength;
        if (bLength == 0)
            return aLength;

        var distances = new int[aLength + 1, bLength + 1];
        for (var index = 0; index <= aLength; index++)
            distances[index, 0] = index;
        for (var index = 0; index <= bLength; index++)
            distances[0, index] = index;

        for (var aIndex = 1; aIndex <= aLength; aIndex++)
        {
            for (var bIndex = 1; bIndex <= bLength; bIndex++)
            {
                var cost = char.ToLowerInvariant(a[aIndex - 1]) == char.ToLowerInvariant(b[bIndex - 1]) ? 0 : 1;
                distances[aIndex, bIndex] = Math.Min(
                    Math.Min(distances[aIndex - 1, bIndex] + 1, distances[aIndex, bIndex - 1] + 1),
                    distances[aIndex - 1, bIndex - 1] + cost);
            }
        }

        return distances[aLength, bLength];
    }
}
