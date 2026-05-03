using System.Text;

namespace Weave.Security.Scanning;

public static class SecretPlaceholderParser
{
    private const string Prefix = "{secret:";

    public static IEnumerable<string> EnumeratePaths(string content)
    {
        var pos = 0;

        while (pos < content.Length)
        {
            var start = content.IndexOf(Prefix, pos, StringComparison.Ordinal);
            if (start < 0)
                break;

            var pathStart = start + Prefix.Length;
            var end = content.IndexOf('}', pathStart);
            if (end < 0)
                break;

            yield return content[pathStart..end];
            pos = end + 1;
        }
    }

    public static string Substitute(string content, Func<string, string?> resolver)
    {
        var pos = 0;
        StringBuilder? sb = null;

        while (pos < content.Length)
        {
            var start = content.IndexOf(Prefix, pos, StringComparison.Ordinal);
            if (start < 0)
                break;

            var pathStart = start + Prefix.Length;
            var end = content.IndexOf('}', pathStart);
            if (end < 0)
                break;

            var path = content[pathStart..end];
            var resolved = resolver(path);

            if (resolved is not null)
            {
                sb ??= new StringBuilder(content.Length);
                sb.Append(content, pos, start - pos);
                sb.Append(resolved);
                pos = end + 1;
            }
            else
            {
                sb ??= new StringBuilder(content.Length);
                sb.Append(content, pos, end + 1 - pos);
                pos = end + 1;
            }
        }

        if (sb is null)
            return content;

        sb.Append(content, pos, content.Length - pos);
        return sb.ToString();
    }
}
