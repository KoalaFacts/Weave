using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

public sealed class WorkspaceShowCommand : AsyncCommand<WorkspaceShowCommand.Settings>
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

        var content = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        AnsiConsole.Write(new Panel(content).Header("workspace.yml").Border(BoxBorder.Rounded));
        return 0;
    }
}

public sealed class WorkspaceValidateCommand : AsyncCommand<WorkspaceValidateCommand.Settings>
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

        try
        {
            var yaml = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var parser = new ManifestParser();
            var manifest = parser.Parse(yaml);
            var errors = parser.Validate(manifest);

            if (errors.Count > 0)
            {
                AnsiConsole.MarkupLine("[red]Configuration invalid:[/]");
                foreach (var error in errors)
                    AnsiConsole.MarkupLine($"  - {error}");

                return 1;
            }

            AnsiConsole.MarkupLine("[green]Configuration valid.[/]");
            AnsiConsole.MarkupLine($"  Name: [bold]{manifest.Name}[/]");
            AnsiConsole.MarkupLine($"  Agents: {manifest.Agents?.Count ?? 0}");
            AnsiConsole.MarkupLine($"  Tools: {manifest.Tools?.Count ?? 0}");
            AnsiConsole.MarkupLine($"  Targets: {manifest.Targets?.Count ?? 0}");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Configuration invalid: {ex.Message}[/]");
            return 1;
        }
    }
}
