namespace Weave.Cli.Commands;

internal static class VersionComparer
{
    public static bool IsNewer(string candidate, string baseline) =>
        Parse(candidate).CompareTo(Parse(baseline)) > 0;

    private static Version Parse(string version)
    {
        var dash = version.IndexOf('-', StringComparison.Ordinal);
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        var cut = -1;
        if (dash >= 0)
            cut = dash;
        if (plus >= 0 && (cut == -1 || plus < cut))
            cut = plus;

        var numeric = cut >= 0 ? version[..cut] : version;
        return Version.TryParse(numeric, out var parsed) ? parsed : new Version(0, 0, 0);
    }
}
