using System.Globalization;
using Spectre.Console;
using Weave.Cli.Commands;

namespace Weave.Cli.Tui;

internal sealed class TuiTasksView(WorkspaceApiClient client)
{
    public async Task RenderAsync(TuiSession session, CancellationToken ct)
    {
        if (!session.IsRunning)
        {
            CliTheme.WriteMuted("Workspace is not running. Start it with /up first.");
            return;
        }

        if (session.AgentName is null)
        {
            CliTheme.WriteMuted("No agent selected. Use /use <agent> first.");
            return;
        }

        IReadOnlyList<ApiTaskResponse> tasks;
        try
        {
            tasks = await client.GetTasksAsync(session.WorkspaceId!, session.AgentName, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or IOException)
        {
            CliTheme.WriteError($"Failed to fetch tasks: {ex.Message}");
            return;
        }

        if (tasks.Count == 0)
        {
            CliTheme.WriteMuted($"No tasks for agent '{session.AgentName}'.");
            return;
        }

        var table = CliTheme.CreateTable($"Tasks · {session.AgentName}");
        table.AddColumn(CliTheme.StyledColumn("ID"));
        table.AddColumn(CliTheme.StyledColumn("Description"));
        table.AddColumn(CliTheme.StyledColumn("Status"));
        table.AddColumn(CliTheme.StyledColumn("Created"));

        foreach (var task in tasks)
        {
            table.AddRow(
                Markup.Escape(task.TaskId),
                Markup.Escape(task.Description),
                TuiMarkup.ColorStatus(task.Status),
                task.CreatedAt.ToString("g", CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);
    }
}
