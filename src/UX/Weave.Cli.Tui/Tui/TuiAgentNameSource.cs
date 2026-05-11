using Weave.Actions.Agent;
using Weave.Actions.Context;

using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tui;

internal sealed class TuiAgentNameSource
{
    private readonly ManifestParser _parser = new();
    private readonly ListAgentsAction _listAgentsAction;

    public TuiAgentNameSource(ListAgentsAction listAgentsAction)
    {
        _listAgentsAction = listAgentsAction;
    }

    public async Task<List<string>> FetchAsync(TuiSession session, CancellationToken ct)
    {
        if (session.IsRunning)
        {
            var result = await _listAgentsAction.ExecuteAsync(
                new ListAgentsInput(session.WorkspaceId!),
                ct);

            if (result.IsSuccess)
                return [.. result.Value.Agents.Select(a => a.AgentName)];

            if (result.Failure.Reason == ActionFailureReason.SiloUnreachable)
                CliTheme.WriteMuted($"Could not reach Silo for agent list ({result.Failure.Message}). Falling back to manifest.");
            else if (result.Failure.Reason != ActionFailureReason.Cancelled)
                CliTheme.WriteMuted($"Silo agent list failed ({result.Failure.Message}). Falling back to manifest.");
        }

        if (session.ManifestPath is null)
            return [];

        try
        {
            var manifest = _parser.Parse(File.ReadAllText(session.ManifestPath));
            return manifest.Agents is null ? [] : [.. manifest.Agents.Keys];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException)
        {
            CliTheme.WriteMuted($"Could not read manifest for agent names ({ex.Message}).");
            return [];
        }
    }
}
