using System.Text;

namespace Weave.Cli.Commands.Local;

internal static class LocalTextLines
{
    public static async Task<string?> ReadAsync(TextReader reader, int limit, bool discardExcess, CancellationToken ct)
    {
        var text = new StringBuilder();
        var character = new char[1];
        var truncated = false;
        while (await reader.ReadAsync(character.AsMemory(), ct) != 0)
        {
            if (character[0] == '\n')
                return text + (truncated ? " [truncated]" : "");
            if (text.Length >= limit)
            {
                if (!discardExcess)
                    throw new ArgumentException("Input exceeds the client limit.");
                truncated = true;
            }
            else
                text.Append(character[0]);
        }
        return text.Length == 0 && !truncated ? null : text + (truncated ? " [truncated]" : "");
    }
}
