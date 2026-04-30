using System.CommandLine;
using Spectre.Console;

namespace Weave.Cli.Commands;

internal static class VersionCommand
{
    public static Command Create()
    {
        var cmd = new Command("version", "Show the installed weave version and cached update info");
        cmd.SetAction(_ =>
        {
            var current = VersionInfo.Current();
            var cache = VersionInfo.LoadCache();

            AnsiConsole.MarkupLine(
                $"weave [bold rgb({CliTheme.Primary.R},{CliTheme.Primary.G},{CliTheme.Primary.B})]v{Markup.Escape(current)}[/]");

            if (cache is null)
            {
                CliTheme.WriteMuted("  (no update cache yet — run `weave upgrade` to check now)");
                return 0;
            }

            var newer = VersionInfo.IsNewer(cache.LatestVersion, current);
            if (newer)
            {
                AnsiConsole.MarkupLine(
                    $"  [rgb({CliTheme.Warning.R},{CliTheme.Warning.G},{CliTheme.Warning.B})]" +
                    $"↑ v{Markup.Escape(cache.LatestVersion)} is available[/] " +
                    $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]" +
                    $"(checked {cache.CheckedAt.ToLocalTime():u})[/]");
                CliTheme.WriteMuted($"  Upgrade:  {VersionInfo.UpgradeCommand}");
            }
            else
            {
                CliTheme.WriteMuted(
                    $"  latest on NuGet: v{cache.LatestVersion} (checked {cache.CheckedAt.ToLocalTime():u})");
            }

            return 0;
        });
        return cmd;
    }
}
