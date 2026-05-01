using System.Globalization;
using Spectre.Console;
using Weave.Cli.Commands;

namespace Weave.Cli.Tui;

internal sealed class TuiAgentListView
{
    private readonly TuiAgentNameSource _agentNameSource;

    public TuiAgentListView(TuiAgentNameSource agentNameSource) => _agentNameSource = agentNameSource;

    public async Task RenderAsync(TuiSession session, CancellationToken ct)
    {
        if (!session.HasWorkspace)
        {
            CliTheme.WriteMuted("No workspace open. Try: /open <workspace>");
            return;
        }

        if (session.IsRunning)
        {
            try
            {
                using var client = new WorkspaceApiClient();
                if (await client.IsReachableAsync(ct))
                {
                    var live = await client.GetAgentsAsync(session.WorkspaceId!, ct);
                    if (live.Count > 0)
                    {
                        RenderLiveAgents(session, live);
                        return;
                    }
                }
            }
            catch (HttpRequestException)
            {
                // Fall through to manifest-only view.
            }
        }

        var names = await _agentNameSource.FetchAsync(session, ct);
        if (names.Count == 0)
        {
            CliTheme.WriteWarning("No agents available.");
            return;
        }

        RenderManifestAgents(session, names);
    }

    private static void RenderLiveAgents(TuiSession session, IReadOnlyList<ApiAgentResponse> agents)
    {
        var table = CliTheme.CreateTable($"Agents · {session.WorkspaceName}");
        table.AddColumn(CliTheme.StyledColumn(""));
        table.AddColumn(CliTheme.StyledColumn("Name"));
        table.AddColumn(CliTheme.StyledColumn("Status"));
        table.AddColumn(CliTheme.StyledColumn("Model"));
        table.AddColumn(CliTheme.StyledColumn("Tasks"));
        table.AddColumn(CliTheme.StyledColumn("Tools"));

        foreach (var agent in agents.OrderBy(a => a.AgentName, StringComparer.Ordinal))
        {
            var marker = string.Equals(agent.AgentName, session.AgentName, StringComparison.Ordinal)
                ? TuiMarkup.ColorTag(CliTheme.Primary, "●")
                : " ";
            table.AddRow(
                marker,
                $"[bold white]{Markup.Escape(agent.AgentName)}[/]",
                TuiMarkup.ColorStatus(agent.Status),
                Markup.Escape(agent.Model ?? "—"),
                agent.ActiveTasks?.Count.ToString(CultureInfo.InvariantCulture) ?? "0",
                agent.ConnectedTools?.Count.ToString(CultureInfo.InvariantCulture) ?? "0");
        }

        AnsiConsole.Write(table);
        if (session.AgentName is null)
            CliTheme.WriteMuted("Pick one with: /use <name>");
    }

    private static void RenderManifestAgents(TuiSession session, IEnumerable<string> agentNames)
    {
        var fallbackTable = CliTheme.CreateTable($"Agents · {session.WorkspaceName}");
        fallbackTable.AddColumn(CliTheme.StyledColumn(""));
        fallbackTable.AddColumn(CliTheme.StyledColumn("Name"));

        foreach (var name in agentNames.OrderBy(n => n, StringComparer.Ordinal))
        {
            var marker = string.Equals(name, session.AgentName, StringComparison.Ordinal)
                ? TuiMarkup.ColorTag(CliTheme.Primary, "●")
                : " ";
            fallbackTable.AddRow(marker, $"[bold white]{Markup.Escape(name)}[/]");
        }

        AnsiConsole.Write(fallbackTable);
        if (session.AgentName is null)
            CliTheme.WriteMuted("Pick one with: /use <name>");
    }
}
