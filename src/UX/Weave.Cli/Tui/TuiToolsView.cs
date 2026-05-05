using Spectre.Console;
using Weave.Cli.Commands;

namespace Weave.Cli.Tui;

internal sealed class TuiToolsView(WorkspaceApiClient client)
{
    public async Task RenderAsync(TuiSession session, CancellationToken ct)
    {
        if (!session.IsRunning)
        {
            CliTheme.WriteMuted("Workspace is not running. Start it with /up first.");
            return;
        }

        IReadOnlyList<ApiToolResponse> tools;
        try
        {
            tools = await client.GetToolsAsync(session.WorkspaceId!, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or IOException)
        {
            CliTheme.WriteError($"Failed to fetch tools: {ex.Message}");
            return;
        }

        if (tools.Count == 0)
        {
            CliTheme.WriteMuted("No tools registered in this workspace.");
            return;
        }

        var table = CliTheme.CreateTable($"Tools · {session.WorkspaceName}");
        table.AddColumn(CliTheme.StyledColumn("Name"));
        table.AddColumn(CliTheme.StyledColumn("Type"));
        table.AddColumn(CliTheme.StyledColumn("Status"));
        table.AddColumn(CliTheme.StyledColumn("Endpoint"));

        foreach (var tool in tools.OrderBy(t => t.ToolName, StringComparer.Ordinal))
        {
            table.AddRow(
                $"[bold white]{Markup.Escape(tool.ToolName)}[/]",
                Markup.Escape(tool.ToolType ?? "—"),
                TuiMarkup.ColorStatus(tool.Status),
                Markup.Escape(tool.Endpoint ?? "—"));
        }

        AnsiConsole.Write(table);
    }
}
