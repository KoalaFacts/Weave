using Spectre.Console;


namespace Weave.Cli.Shell;

internal static class StatusMarkup
{
    public static string ColorStatus(string status)
    {
        var lower = status.ToLowerInvariant();
        var tint = lower switch
        {
            "running" or "active" or "connected" or "ready" or "healthy" => CliTheme.Success,
            "starting" or "connecting" or "pending" => CliTheme.Info,
            "stopped" or "idle" or "disconnected" => CliTheme.Muted,
            _ when lower.Contains("error", StringComparison.Ordinal)
                   || lower.Contains("fail", StringComparison.Ordinal) => CliTheme.Error,
            _ => CliTheme.Warning,
        };

        return ColorTag(tint, status);
    }

    public static string ColorTag(Color tint, string text)
        => $"[rgb({tint.R},{tint.G},{tint.B})]{Markup.Escape(text)}[/]";
}
