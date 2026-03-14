using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

public sealed class WorkspaceStatusCommand : AsyncCommand<WorkspaceStatusCommand.Settings>
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
            AnsiConsole.MarkupLine($"[red]No workspace.json found for '{settings.Name}'.[/]");
            return 1;
        }

        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var parser = new ManifestParser();
        var manifest = parser.Parse(json);

        var statePath = WorkspaceApiClient.GetWorkspaceStatePath(manifestPath);
        if (File.Exists(statePath))
        {
            var workspaceId = (await File.ReadAllTextAsync(statePath, cancellationToken)).Trim();
            try
            {
                using var client = new WorkspaceApiClient();
                var workspace = await client.GetWorkspaceAsync(workspaceId, cancellationToken);
                var agents = await client.GetAgentsAsync(workspaceId, cancellationToken);
                var tools = await client.GetToolsAsync(workspaceId, cancellationToken);

                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn("Property");
                table.AddColumn("Value");
                table.AddRow("Workspace", $"[bold]{manifest.Name}[/]");
                table.AddRow("Workspace ID", workspace.WorkspaceId);
                table.AddRow("Status", workspace.Status);
                table.AddRow("Manifest", manifestPath);
                table.AddRow("Containers", workspace.ContainerCount.ToString(CultureInfo.InvariantCulture));
                AnsiConsole.Write(table);

                if (agents.Count > 0)
                {
                    AnsiConsole.WriteLine();
                    var agentTable = new Table().Title("[bold]Agents[/]").Border(TableBorder.Rounded);
                    agentTable.AddColumn("Name");
                    agentTable.AddColumn("Status");
                    agentTable.AddColumn("Model");
                    agentTable.AddColumn("Tools");

                    foreach (var agent in agents.OrderBy(a => a.AgentName, StringComparer.Ordinal))
                    {
                        agentTable.AddRow(
                            agent.AgentName,
                            agent.Status,
                            agent.Model ?? string.Empty,
                            string.Join(", ", agent.ConnectedTools));
                    }

                    AnsiConsole.Write(agentTable);
                }

                if (tools.Count > 0)
                {
                    AnsiConsole.WriteLine();
                    var toolTable = new Table().Title("[bold]Tools[/]").Border(TableBorder.Rounded);
                    toolTable.AddColumn("Name");
                    toolTable.AddColumn("Type");
                    toolTable.AddColumn("Status");

                    foreach (var tool in tools.OrderBy(t => t.ToolName, StringComparer.Ordinal))
                        toolTable.AddRow(tool.ToolName, tool.ToolType, tool.Status);

                    AnsiConsole.Write(toolTable);
                }

                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Live status unavailable: {ex.Message}. Falling back to manifest data.[/]");
            }
        }

        var manifestTable = new Table().Border(TableBorder.Rounded);
        manifestTable.AddColumn("Property");
        manifestTable.AddColumn("Value");
        manifestTable.AddRow("Workspace", $"[bold]{manifest.Name}[/]");
        manifestTable.AddRow("Version", manifest.Version);
        manifestTable.AddRow("Manifest", manifestPath);
        AnsiConsole.Write(manifestTable);

        if (manifest.Agents is { Count: > 0 })
        {
            AnsiConsole.WriteLine();
            var agentTable = new Table().Title("[bold]Agents (manifest)[/]").Border(TableBorder.Rounded);
            agentTable.AddColumn("Name");
            agentTable.AddColumn("Model");
            agentTable.AddColumn("Tools");

            foreach (var (name, agent) in manifest.Agents)
                agentTable.AddRow(name, agent.Model, string.Join(", ", agent.Tools));

            AnsiConsole.Write(agentTable);
        }

        if (manifest.Tools is { Count: > 0 })
        {
            AnsiConsole.WriteLine();
            var toolTable = new Table().Title("[bold]Tools (manifest)[/]").Border(TableBorder.Rounded);
            toolTable.AddColumn("Name");
            toolTable.AddColumn("Type");

            foreach (var (name, tool) in manifest.Tools)
                toolTable.AddRow(name, tool.Type);

            AnsiConsole.Write(toolTable);
        }

        return 0;
    }
}
