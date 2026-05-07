using Weave.Actions.Agent;
using Weave.Actions.Context;

using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tui;

internal sealed class TuiAgentSelector
{
    private readonly ManifestParser _parser = new();
    private readonly TuiAgentNameSource _agentNameSource;
    private readonly SelectAgentAction _selectAction;

    public TuiAgentSelector(TuiAgentNameSource agentNameSource, SelectAgentAction selectAction)
    {
        _agentNameSource = agentNameSource;
        _selectAction = selectAction;
    }

    public async Task SelectAsync(
        TuiSession session,
        string? arg,
        Action clearConversationHistory,
        CancellationToken ct)
    {
        if (!session.HasWorkspace)
        {
            CliTheme.WriteMuted("No workspace open. Try: /open <workspace>");
            return;
        }

        var agents = await _agentNameSource.FetchAsync(session, ct);
        var result = await _selectAction.ExecuteAsync(new SelectAgentInput(agents, arg), ct);
        if (!result.IsSuccess)
        {
            switch (result.Failure.Reason)
            {
                case ActionFailureReason.Cancelled:
                    return;
                case ActionFailureReason.NotFound when agents.Count == 0:
                    CliTheme.WriteWarning(result.Failure.Message);
                    return;
                case ActionFailureReason.NotFound:
                    CliTheme.WriteError($"{result.Failure.Message.TrimEnd('.')} in '{session.WorkspaceName}'.");
                    return;
                default:
                    CliTheme.WriteError(result.Failure.Message);
                    return;
            }
        }

        session.AgentName = result.Value.AgentName;
        clearConversationHistory();
        CliTheme.WriteMuted($"Agent set to '{result.Value.AgentName}'.");
        TuiNextStepHint.Render(session);
    }

    public void TrySelectOnlyAgent(TuiSession session)
    {
        if (session.ManifestPath is null)
            return;

        try
        {
            var manifest = _parser.Parse(File.ReadAllText(session.ManifestPath));
            if (manifest.Agents is { Count: 1 } agents)
                session.AgentName = agents.Keys.First();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException)
        {
            CliTheme.WriteMuted($"Could not auto-select agent ({ex.Message}).");
        }
    }
}
