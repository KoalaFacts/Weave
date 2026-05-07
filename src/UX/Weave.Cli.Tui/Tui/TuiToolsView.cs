using Weave.Actions.Context;
using Weave.Actions.Tool;


namespace Weave.Cli.Tui;

internal sealed class TuiToolsView
{
    private readonly ListToolsAction _action;

    public TuiToolsView(ListToolsAction action)
    {
        _action = action;
    }

    public async Task RenderAsync(TuiSession session, CancellationToken ct)
    {
        if (!session.IsRunning)
        {
            CliTheme.WriteMuted("Workspace is not running. Start it with /up first.");
            return;
        }

        var result = await _action.ExecuteAsync(new ListToolsInput(session.WorkspaceId!), ct);
        if (!result.IsSuccess)
        {
            if (result.Failure.Reason == ActionFailureReason.Cancelled)
                return;
            CliTheme.WriteError($"Failed to fetch tools: {result.Failure.Message}");
            return;
        }

        if (result.Value.Tools.Count == 0)
        {
            CliTheme.WriteMuted("No tools registered in this workspace.");
            return;
        }

        ToolsRenderer.RenderLive(session.WorkspaceName!, result.Value.Tools);
    }
}
