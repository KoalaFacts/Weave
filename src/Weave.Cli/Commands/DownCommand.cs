using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Weave.Cli.Commands;

public sealed class DownCommand : AsyncCommand<DownCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--workspace <WORKSPACE>")]
        [Description("Workspace name")]
        public string? Workspace { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var manifestPath = ManifestResolver.Resolve(settings.Workspace);
        if (manifestPath is null)
        {
            AnsiConsole.MarkupLine("[red]No workspace.yml found.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine("Stopping workspace...");
        await Task.Delay(100, cancellationToken); // placeholder for actual teardown
        AnsiConsole.MarkupLine("[green]Workspace stopped.[/]");
        return 0;
    }
}
