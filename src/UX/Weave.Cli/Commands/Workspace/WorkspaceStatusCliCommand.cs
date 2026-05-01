using System.Globalization;
using Spectre.Console;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal sealed class WorkspaceStatusCliCommand(WorkspaceManifestFile? manifests = null) : ICliCommand<WorkspaceNameOptions>
{
    private readonly WorkspaceManifestFile _manifests = manifests ?? new WorkspaceManifestFile();

    public string Name => "status";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Show workspace status";

    public async Task<int> ExecuteAsync(WorkspaceNameOptions options, CancellationToken ct)
    {
        var name = WorkspacePrompt.SelectName(options.Name, "Which workspace would you like to inspect?");
        var manifestPath = ManifestResolver.Resolve(name);
        if (manifestPath is null)
        {
            CliTheme.WriteError(name is null
                ? "No workspace.json found. Create one first with: weave workspace new"
                : $"No workspace.json found for '{name}'.");
            return 1;
        }

        var manifest = await _manifests.ReadAsync(manifestPath, ct);

        var statePath = WorkspaceApiClient.GetWorkspaceStatePath(manifestPath);
        if (File.Exists(statePath))
        {
            var workspaceId = (await File.ReadAllTextAsync(statePath, ct)).Trim();
            try
            {
                using var client = new WorkspaceApiClient();
                var workspace = await client.GetWorkspaceAsync(workspaceId, ct);
                var agents = await client.GetAgentsAsync(workspaceId, ct);
                var tools = await client.GetToolsAsync(workspaceId, ct);

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
                    RenderAgents(agents);

                if (tools.Count > 0)
                    RenderTools(tools);

                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[rgb({CliTheme.Warning.R},{CliTheme.Warning.G},{CliTheme.Warning.B})]{CliTheme.IconWarning} Live status unavailable: {Markup.Escape(ex.Message)}. Falling back to manifest data.[/]");
            }
        }

        RenderManifestStatus(manifest, manifestPath);
        return 0;
    }

    private static void RenderAgents(IReadOnlyList<ApiAgentResponse> agents)
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

    private static void RenderTools(IReadOnlyList<ApiToolResponse> tools)
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

    private static void RenderManifestStatus(WorkspaceManifest manifest, string manifestPath)
    {
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
    }
}
