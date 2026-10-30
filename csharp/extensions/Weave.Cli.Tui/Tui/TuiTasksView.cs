using Weave.Actions.AgentTask;
using Weave.Actions.Context;
using Weave.Cli.Tui.Verbs;

namespace Weave.Cli.Tui;

internal sealed class TuiTasksView(ListTasksAction action) : ITuiVerb
{
    public string Name => "tasks";

    public IReadOnlyList<string> Aliases => [];

    public async Task DispatchAsync(TuiVerbContext context, CancellationToken ct)
    {
        var session = context.Session;
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

        var result = await action.ExecuteAsync(new ListTasksInput(session.WorkspaceId!, session.AgentName), ct);
        if (!result.IsSuccess)
        {
            if (result.Failure.Reason == ActionFailureReason.Cancelled)
                return;
            CliTheme.WriteError($"Failed to fetch tasks: {result.Failure.Message}");
            return;
        }

        if (result.Value.Tasks.Count == 0)
        {
            CliTheme.WriteMuted($"No tasks for agent '{session.AgentName}'.");
            return;
        }

        TasksRenderer.RenderLive(session.AgentName, result.Value.Tasks);
    }
}
