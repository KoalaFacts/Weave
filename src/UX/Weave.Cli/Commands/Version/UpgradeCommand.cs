using System.CommandLine;
using Spectre.Console;

namespace Weave.Cli.Commands;

internal static class UpgradeCommand
{
    public static Command Create()
    {
        var cmd = new Command("upgrade", "Check NuGet for a newer weave release");
        cmd.SetAction(async (_, cancellationToken) =>
        {
            var result = await VersionInfo.CheckAsync(cancellationToken);

            CliTheme.WriteKeyValue("Installed", $"v{result.Current}");
            if (result.Latest is not null)
                CliTheme.WriteKeyValue("Latest", $"v{result.Latest}");

            if (result.UpdateAvailable && result.Latest is not null)
            {
                AnsiConsole.MarkupLine(
                    $"[rgb({CliTheme.Warning.R},{CliTheme.Warning.G},{CliTheme.Warning.B})]" +
                    $"↑ v{Markup.Escape(result.Latest)} is available.[/]");
                CliTheme.WriteMuted($"  Upgrade:  {VersionInfo.UpgradeCommand}");
                return 0;
            }

            if (result.Latest is not null)
            {
                CliTheme.WriteSuccess("You are on the latest version.");
                return 0;
            }

            if (result.Note is not null)
                CliTheme.WriteWarning(result.Note);

            return 1;
        });
        return cmd;
    }
}
