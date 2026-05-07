using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;
using Weave.Actions.Agent;
using Weave.Actions.Tool;
using Weave.Actions.Workspace;

using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tui;

internal static class TuiLiveStatusRenderer
{
    public static Rows Build(
        string manifestPath,
        WorkspaceManifest manifest,
        WorkspaceStatusSummary workspace,
        IReadOnlyList<AgentSummary> agents,
        IReadOnlyList<ToolSummary> tools)
    {
        var summary = CliTheme.CreateTable("Live Status");
        summary.AddColumn(CliTheme.StyledColumn("Property"));
        summary.AddColumn(CliTheme.StyledColumn("Value"));
        summary.AddRow("Workspace", $"[bold white]{Markup.Escape(manifest.Name)}[/]");
        summary.AddRow("Workspace ID", Markup.Escape(workspace.WorkspaceId));
        summary.AddRow("Status", StatusMarkup.ColorStatus(workspace.Status));
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

    private static void AddAgentStatusTable(List<IRenderable> rows, IReadOnlyList<AgentSummary> agents)
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
            agentTable.AddRow(
                $"[bold white]{Markup.Escape(agent.AgentName)}[/]",
                StatusMarkup.ColorStatus(agent.Status),
                Markup.Escape(agent.Model ?? string.Empty),
                agent.ActiveTasksCount.ToString(CultureInfo.InvariantCulture),
                agent.ConnectedToolsCount.ToString(CultureInfo.InvariantCulture));
        }

        rows.Add(agentTable);
    }

    private static void AddToolStatusTable(List<IRenderable> rows, IReadOnlyList<ToolSummary> tools)
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
                StatusMarkup.ColorStatus(tool.Status));

        rows.Add(toolTable);
    }
}
