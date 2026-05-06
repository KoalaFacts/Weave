using System.Globalization;
using Spectre.Console;
using Weave.Actions.AgentTask;
using Weave.Cli.Tui;

namespace Weave.Cli.Commands;

/// <summary>
/// Shared Spectre rendering for <see cref="ListTasksResult"/>. Both the
/// <see cref="TasksCliCommand"/> and the migrated TUI <c>/tasks</c> slash
/// bottom out here so the table layout is owned in one place.
/// </summary>
internal static class TasksRenderer
{
    public static void RenderLive(string agentName, IReadOnlyList<TaskSummary> tasks)
    {
        var table = CliTheme.CreateTable($"Tasks · {agentName}");
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
