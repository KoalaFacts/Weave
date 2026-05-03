using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;
using Weave.Cli.Commands;
using Weave.Workspaces.Models;

namespace Weave.Cli.Tui;

internal static class TuiLiveStatusRenderer
{
    public static Rows Build(
        string manifestPath,
        WorkspaceManifest manifest,
        ApiWorkspaceResponse workspace,
        IReadOnlyList<ApiAgentResponse> agents,
        IReadOnlyList<ApiToolResponse> tools)
    {
        var summary = CliTheme.CreateTable("Live Status");
        summary.AddColumn(CliTheme.StyledColumn("Property"));
        summary.AddColumn(CliTheme.StyledColumn("Value"));
        summary.AddRow("Workspace", $"[bold white]{Markup.Escape(manifest.Name)}[/]");
        summary.AddRow("Workspace ID", Markup.Escape(workspace.WorkspaceId));
        summary.AddRow("Status", TuiMarkup.ColorStatus(workspace.Status));
        summary.AddRow("Containers", workspace.ContainerCount.ToString(CultureInfo.InvariantCulture));
        if (workspace.StartedAt is { } started)
            summary.AddRow("Started", started.ToLocalTime().ToString("u", CultureInfo.InvariantCulture));
        summary.AddRow("Manifest", Markup.Escape(manifestPath));
        summary.AddRow("Refreshed", DateTime.Now.ToString("T", CultureInfo.InvariantCulture));

        var rows = new List<IRenderable> { summary };
        AddAgentStatusTable(rows, agents);
        AddToolStatusTable(rows, tools);
        return new Rows(rows);
    }

    private static void AddAgentStatusTable(List<IRenderable> rows, IReadOnlyList<ApiAgentResponse> agents)
    {
        if (agents.Count == 0)
            return;

        var agentTable = CliTheme.CreateTable("Agents");
        agentTable.AddColumn(CliTheme.StyledColumn("Name"));
        agentTable.AddColumn(CliTheme.StyledColumn("Status"));
        agentTable.AddColumn(CliTheme.StyledColumn("Model"));
        agentTable.AddColumn(CliTheme.StyledColumn("Active Tasks"));
        agentTable.AddColumn(CliTheme.StyledColumn("Tools"));

        foreach (var agent in agents.OrderBy(agent => agent.AgentName, StringComparer.Ordinal))
        {
            var taskSummary = agent.ActiveTasks.Count == 0
                ? "—"
                : string.Join(", ", agent.ActiveTasks.Select(task => task.Description));

            agentTable.AddRow(
                $"[bold white]{Markup.Escape(agent.AgentName)}[/]",
                TuiMarkup.ColorStatus(agent.Status),
                Markup.Escape(agent.Model ?? string.Empty),
                Markup.Escape(taskSummary),
                Markup.Escape(string.Join(", ", agent.ConnectedTools)));
        }

        rows.Add(agentTable);
    }

    private static void AddToolStatusTable(List<IRenderable> rows, IReadOnlyList<ApiToolResponse> tools)
    {
        if (tools.Count == 0)
            return;

        var toolTable = CliTheme.CreateTable("Tools");
        toolTable.AddColumn(CliTheme.StyledColumn("Name"));
        toolTable.AddColumn(CliTheme.StyledColumn("Type"));
        toolTable.AddColumn(CliTheme.StyledColumn("Status"));

        foreach (var tool in tools.OrderBy(tool => tool.ToolName, StringComparer.Ordinal))
            toolTable.AddRow(
                $"[bold white]{Markup.Escape(tool.ToolName)}[/]",
                Markup.Escape(tool.ToolType),
                TuiMarkup.ColorStatus(tool.Status));

        rows.Add(toolTable);
    }
}
