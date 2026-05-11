using Weave.Actions.Agent;
using Weave.Actions.Context;
using Weave.Cli.Tui.Verbs;

namespace Weave.Cli.Tui;

internal sealed class TuiAgentListView(
    TuiAgentNameSource agentNameSource,
    ListAgentsAction action) : ITuiVerb
{
    public string Name => "agents";

    public IReadOnlyList<string> Aliases => [];

    public async Task DispatchAsync(TuiVerbContext context, CancellationToken ct)
    {
        var session = context.Session;
        if (!session.HasWorkspace)
        {
            CliTheme.WriteMuted("No workspace open. Try: /open <workspace>");
            return;
        }

        if (session.IsRunning)
        {
            var result = await action.ExecuteAsync(new ListAgentsInput(session.WorkspaceId!), ct);
            if (result.IsSuccess && result.Value.Agents.Count > 0)
            {
                AgentsRenderer.RenderLive(session.WorkspaceName!, result.Value.Agents, session.AgentName);
                if (session.AgentName is null)
                    CliTheme.WriteMuted("Pick one with: /use <name>");
                return;
            }
            if (!result.IsSuccess && result.Failure.Reason == ActionFailureReason.Cancelled)
                return;
            // Either silo unreachable or zero live agents — fall through to
            // manifest-only view (the TUI's UX concession).
        }

        var names = await agentNameSource.FetchAsync(session, ct);
        if (names.Count == 0)
        {
            CliTheme.WriteWarning("No agents available.");
            return;
        }

        AgentsRenderer.RenderManifestNames(session.WorkspaceName!, names, session.AgentName);
        if (session.AgentName is null)
            CliTheme.WriteMuted("Pick one with: /use <name>");
    }
}
