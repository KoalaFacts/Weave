using System.Globalization;
using Spectre.Console;
using Weave.Actions.Agent;
using Weave.Cli.Tui;

namespace Weave.Cli.Commands;

/// <summary>
/// Shared Spectre rendering for <see cref="ListAgentsResult"/>. Both the new
/// <see cref="AgentsCliCommand"/> (advanced + guided modes) and the migrated
/// TUI <c>/agents</c> slash bottom out here so the table layout is owned in
/// one place. The currently-selected-agent marker is optional — the CLI
/// command passes <c>null</c>, the TUI passes the session's current agent.
/// </summary>
internal static class AgentsRenderer
{
    public static void RenderLive(string workspaceName, IReadOnlyList<AgentSummary> agents, string? selectedAgent)
    {
        var table = CliTheme.CreateTable($"Agents · {workspaceName}");
        table.AddColumn(CliTheme.StyledColumn(""));
        table.AddColumn(CliTheme.StyledColumn("Name"));
        table.AddColumn(CliTheme.StyledColumn("Status"));
        table.AddColumn(CliTheme.StyledColumn("Model"));
        table.AddColumn(CliTheme.StyledColumn("Tasks"));
        table.AddColumn(CliTheme.StyledColumn("Tools"));

        foreach (var agent in agents.OrderBy(a => a.AgentName, StringComparer.Ordinal))
        {
            var marker = selectedAgent is not null && string.Equals(agent.AgentName, selectedAgent, StringComparison.Ordinal)
                ? TuiMarkup.ColorTag(CliTheme.Primary, "●")
                : " ";
            table.AddRow(
                marker,
                $"[bold white]{Markup.Escape(agent.AgentName)}[/]",
                TuiMarkup.ColorStatus(agent.Status),
                Markup.Escape(agent.Model ?? "—"),
                agent.ActiveTasksCount.ToString(CultureInfo.InvariantCulture),
                agent.ConnectedToolsCount.ToString(CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);
    }

    public static void RenderManifestNames(string workspaceName, IEnumerable<string> agentNames, string? selectedAgent)
    {
        var table = CliTheme.CreateTable($"Agents · {workspaceName}");
        table.AddColumn(CliTheme.StyledColumn(""));
        table.AddColumn(CliTheme.StyledColumn("Name"));

        foreach (var name in agentNames.OrderBy(n => n, StringComparer.Ordinal))
        {
            var marker = selectedAgent is not null && string.Equals(name, selectedAgent, StringComparison.Ordinal)
                ? TuiMarkup.ColorTag(CliTheme.Primary, "●")
                : " ";
            table.AddRow(marker, $"[bold white]{Markup.Escape(name)}[/]");
        }

        AnsiConsole.Write(table);
    }
}
