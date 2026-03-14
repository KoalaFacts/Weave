using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Weave.Cli.Commands;

public sealed class WorkspaceDownCommand : AsyncCommand<WorkspaceDownCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Workspace name")]
        public string Name { get; init; } = string.Empty;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var manifestPath = ManifestResolver.Resolve(settings.Name);
        if (manifestPath is null)
        {
            AnsiConsole.MarkupLine($"[red]No workspace.yml found for '{settings.Name}'.[/]");
            return 1;
        }

        var statePath = WorkspaceApiClient.GetWorkspaceStatePath(manifestPath);
        if (!File.Exists(statePath))
        {
            AnsiConsole.MarkupLine("[red]Workspace is not running locally. No workspace id was found.[/]");
            return 1;
        }

        var workspaceId = (await File.ReadAllTextAsync(statePath, cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            AnsiConsole.MarkupLine("[red]Workspace state file is empty.[/]");
            return 1;
        }

        try
        {
            using var client = new WorkspaceApiClient();
            await client.StopWorkspaceAsync(workspaceId, cancellationToken);
            File.Delete(statePath);
            AnsiConsole.MarkupLine($"[green]Workspace '{workspaceId}' stopped.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to stop workspace: {ex.Message}[/]");
            return 1;
        }

        return 0;
    }
}
