using Weave.Actions.Context;
using Weave.Actions.Tool;
using Weave.Cli.Tui.Verbs;

namespace Weave.Cli.Tui;

internal sealed class TuiToolsView(ListToolsAction action) : ITuiVerb
{
    public string Name => "tools";

    public IReadOnlyList<string> Aliases => [];

    public async Task DispatchAsync(TuiVerbContext context, CancellationToken ct)
    {
        var session = context.Session;
        if (!session.IsRunning)
        {
            CliTheme.WriteMuted("Workspace is not running. Start it with /up first.");
            return;
        }

        var result = await action.ExecuteAsync(new ListToolsInput(session.WorkspaceId!), ct);
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
