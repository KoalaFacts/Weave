using Spectre.Console;

namespace Weave.Cli.Commands;

internal sealed class UpgradeCliCommand(VersionService versionService) : ICliCommand<NoCliOptions>
{
    public string Name => "upgrade";

    public IReadOnlyList<string> Aliases => ["update"];

    public string Description => "Check NuGet for a newer weave release";

    public async Task<int> ExecuteAsync(NoCliOptions options, CancellationToken ct)
    {
        var result = await versionService.CheckAsync(ct);

        CliTheme.WriteKeyValue("Installed", $"v{result.Current}");
        if (result.Latest is not null)
            CliTheme.WriteKeyValue("Latest", $"v{result.Latest}");

        if (result.UpdateAvailable && result.Latest is not null)
        {
            AnsiConsole.MarkupLine(
                $"[rgb({CliTheme.Warning.R},{CliTheme.Warning.G},{CliTheme.Warning.B})]" +
                $"↑ v{Markup.Escape(result.Latest)} is available.[/]");
            CliTheme.WriteMuted($"  Upgrade:  {VersionService.UpgradeCommand}");
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
    }
}
