using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

public sealed class StatusCommand : AsyncCommand<StatusCommand.Settings>
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

        var yaml = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var parser = new ManifestParser();
        var manifest = parser.Parse(yaml);

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Property");
        table.AddColumn("Value");
        table.AddRow("Workspace", $"[bold]{manifest.Name}[/]");
        table.AddRow("Version", manifest.Version);
        table.AddRow("Manifest", manifestPath);
        AnsiConsole.Write(table);

        if (manifest.Agents is { Count: > 0 })
        {
            AnsiConsole.WriteLine();
            var agentTable = new Table().Title("[bold]Agents[/]").Border(TableBorder.Rounded);
            agentTable.AddColumn("Name");
            agentTable.AddColumn("Model");
            agentTable.AddColumn("Tools");

            foreach (var (name, agent) in manifest.Agents)
            {
                agentTable.AddRow(name, agent.Model, string.Join(", ", agent.Tools));
            }

            AnsiConsole.Write(agentTable);
        }

        if (manifest.Tools is { Count: > 0 })
        {
            AnsiConsole.WriteLine();
            var toolTable = new Table().Title("[bold]Tools[/]").Border(TableBorder.Rounded);
            toolTable.AddColumn("Name");
            toolTable.AddColumn("Type");

            foreach (var (name, tool) in manifest.Tools)
            {
                toolTable.AddRow(name, tool.Type);
            }

            AnsiConsole.Write(toolTable);
        }

        return 0;
    }
}
