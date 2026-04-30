using System.CommandLine;
using System.Globalization;
using Spectre.Console;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

internal static class WorkspaceStatusCommand
{
    public static Command Create()
    {
        var nameArg = new Argument<string>("name") { Description = "Workspace name" };
        nameArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);

        var cmd = new Command("status", "Show workspace status") { nameArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArg)!;

            var manifestPath = ManifestResolver.Resolve(name);
            if (manifestPath is null)
            {
                CliTheme.WriteError($"No workspace.json found for '{name}'.");
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

                    var table = CliTheme.CreateTable();
                    table.AddColumn(CliTheme.StyledColumn("Property"));
                    table.AddColumn(CliTheme.StyledColumn("Value"));
                    table.AddRow("Workspace", $"[bold white]{manifest.Name}[/]");
                    table.AddRow("Workspace ID", workspace.WorkspaceId);
                    table.AddRow("Status", workspace.Status);
                    table.AddRow("Manifest", manifestPath);
                    table.AddRow("Containers", workspace.ContainerCount.ToString(CultureInfo.InvariantCulture));
                    AnsiConsole.Write(table);

                    if (agents.Count > 0)
                    {
                        CliTheme.WriteSection("Agents");
                        var agentTable = CliTheme.CreateTable();
                        agentTable.AddColumn(CliTheme.StyledColumn("Name"));
                        agentTable.AddColumn(CliTheme.StyledColumn("Status"));
                        agentTable.AddColumn(CliTheme.StyledColumn("Model"));
                        agentTable.AddColumn(CliTheme.StyledColumn("Tools"));

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
                        CliTheme.WriteSection("Tools");
                        var toolTable = CliTheme.CreateTable();
                        toolTable.AddColumn(CliTheme.StyledColumn("Name"));
                        toolTable.AddColumn(CliTheme.StyledColumn("Type"));
                        toolTable.AddColumn(CliTheme.StyledColumn("Status"));

                        foreach (var tool in tools.OrderBy(t => t.ToolName, StringComparer.Ordinal))
                            toolTable.AddRow(tool.ToolName, tool.ToolType, tool.Status);

                        AnsiConsole.Write(toolTable);
                    }

                    return 0;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[rgb({CliTheme.Warning.R},{CliTheme.Warning.G},{CliTheme.Warning.B})]{CliTheme.IconWarning} Live status unavailable: {Markup.Escape(ex.Message)}. Falling back to manifest data.[/]");
                }
            }

            var manifestTable = CliTheme.CreateTable();
            manifestTable.AddColumn(CliTheme.StyledColumn("Property"));
            manifestTable.AddColumn(CliTheme.StyledColumn("Value"));
            manifestTable.AddRow("Workspace", $"[bold white]{manifest.Name}[/]");
            manifestTable.AddRow("Version", manifest.Version);
            manifestTable.AddRow("Manifest", manifestPath);
            AnsiConsole.Write(manifestTable);

            if (manifest.Agents is { Count: > 0 })
            {
                CliTheme.WriteSection("Agents (manifest)");
                var agentTable = CliTheme.CreateTable();
                agentTable.AddColumn(CliTheme.StyledColumn("Name"));
                agentTable.AddColumn(CliTheme.StyledColumn("Model"));
                agentTable.AddColumn(CliTheme.StyledColumn("Tools"));

                foreach (var (agentName, agent) in manifest.Agents)
                    agentTable.AddRow(agentName, agent.Model, string.Join(", ", agent.Tools));

                AnsiConsole.Write(agentTable);
            }

            if (manifest.Tools is { Count: > 0 })
            {
                CliTheme.WriteSection("Tools (manifest)");
                var toolTable = CliTheme.CreateTable();
                toolTable.AddColumn(CliTheme.StyledColumn("Name"));
                toolTable.AddColumn(CliTheme.StyledColumn("Type"));

                foreach (var (toolName, tool) in manifest.Tools)
                    toolTable.AddRow(toolName, tool.Type);

                AnsiConsole.Write(toolTable);
            }

            return 0;
        });

        return cmd;
    }
}
