using System.Globalization;
using Spectre.Console;
using Weave.Actions.Agent;
using Weave.Actions.Context;
using Weave.Actions.Tool;
using Weave.Actions.Workspace;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

/// <summary>
/// Phase 1 read-only verb on the CLI side. Composes
/// <see cref="GetWorkspaceStatusAction"/> + <see cref="ListAgentsAction"/>
/// + <see cref="ListToolsAction"/> for the live view, and falls back to the
/// manifest when the workspace isn't running or the silo can't be reached.
/// </summary>
internal sealed class WorkspaceStatusCliCommand : ICliCommand<WorkspaceNameOptions>
{
    private readonly GetWorkspaceStatusAction _statusAction;
    private readonly ListAgentsAction _agentsAction;
    private readonly ListToolsAction _toolsAction;

    public WorkspaceStatusCliCommand(
        GetWorkspaceStatusAction statusAction,
        ListAgentsAction agentsAction,
        ListToolsAction toolsAction)
    {
        _statusAction = statusAction;
        _agentsAction = agentsAction;
        _toolsAction = toolsAction;
    }

    public string Name => "status";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Show workspace status";

    public async Task<int> ExecuteAsync(WorkspaceNameOptions options, CancellationToken ct)
    {
        var name = WorkspacePrompt.SelectName(options.Name, "Which workspace would you like to inspect?");
        var manifestPath = ManifestResolver.Resolve(name);
        if (manifestPath is null)
        {
            WorkspacePrompt.WriteManifestNotFound(name);
            return 1;
        }

        var manifest = await WorkspaceManifestFile.ReadAsync(manifestPath, ct);

        var statePath = WorkspaceApiClient.GetWorkspaceStatePath(manifestPath);
        if (File.Exists(statePath))
        {
            var workspaceId = (await File.ReadAllTextAsync(statePath, ct)).Trim();
            var statusResult = await _statusAction.ExecuteAsync(new GetWorkspaceStatusInput(workspaceId), ct);
            if (statusResult.IsSuccess)
            {
                RenderStatusTable(manifest.Name, statusResult.Value.Workspace, manifestPath);

                var agentsResult = await _agentsAction.ExecuteAsync(new ListAgentsInput(workspaceId), ct);
                if (agentsResult.IsSuccess && agentsResult.Value.Agents.Count > 0)
                    RenderAgents(agentsResult.Value.Agents);

                var toolsResult = await _toolsAction.ExecuteAsync(new ListToolsInput(workspaceId), ct);
                if (toolsResult.IsSuccess && toolsResult.Value.Tools.Count > 0)
                    RenderTools(toolsResult.Value.Tools);

                return 0;
            }

            if (statusResult.Failure.Reason == ActionFailureReason.Cancelled)
                return 130;

            CliTheme.WriteWarning($"{statusResult.Failure.Message} Falling back to manifest data.");
        }

        RenderManifestStatus(manifest, manifestPath);
        return 0;
    }

    private static void RenderStatusTable(string workspaceName, WorkspaceStatusSummary workspace, string manifestPath)
    {
        var table = CliTheme.CreateTable();
        table.AddColumn(CliTheme.StyledColumn("Property"));
        table.AddColumn(CliTheme.StyledColumn("Value"));
        table.AddRow("Workspace", $"[bold white]{Markup.Escape(workspaceName)}[/]");
        table.AddRow("Workspace ID", Markup.Escape(workspace.WorkspaceId));
        table.AddRow("Status", Markup.Escape(workspace.Status));
        table.AddRow("Manifest", Markup.Escape(manifestPath));
        table.AddRow("Containers", workspace.ContainerCount.ToString(CultureInfo.InvariantCulture));
        AnsiConsole.Write(table);
    }

    private static void RenderAgents(IReadOnlyList<AgentSummary> agents)
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
                Markup.Escape(agent.AgentName),
                Markup.Escape(agent.Status),
                Markup.Escape(agent.Model ?? string.Empty),
                agent.ConnectedToolsCount.ToString(CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(agentTable);
    }

    private static void RenderTools(IReadOnlyList<ToolSummary> tools)
    {
        CliTheme.WriteSection("Tools");
        var toolTable = CliTheme.CreateTable();
        toolTable.AddColumn(CliTheme.StyledColumn("Name"));
        toolTable.AddColumn(CliTheme.StyledColumn("Type"));
        toolTable.AddColumn(CliTheme.StyledColumn("Status"));

        foreach (var tool in tools.OrderBy(t => t.ToolName, StringComparer.Ordinal))
            toolTable.AddRow(Markup.Escape(tool.ToolName), Markup.Escape(tool.ToolType), Markup.Escape(tool.Status));

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
