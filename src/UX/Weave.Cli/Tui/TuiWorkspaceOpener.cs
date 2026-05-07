using Spectre.Console;
using Weave.Actions.Context;
using Weave.Actions.Workspace;
using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tui;

internal sealed class TuiWorkspaceOpener
{
    private readonly ManifestParser _parser = new();
    private readonly OpenWorkspaceAction _openAction;
    private readonly TuiAgentSelector _agentSelector;
    private readonly TuiLiveStatusView _liveStatus;

    public TuiWorkspaceOpener(
        OpenWorkspaceAction openAction,
        TuiAgentSelector agentSelector,
        TuiLiveStatusView liveStatus)
    {
        _openAction = openAction;
        _agentSelector = agentSelector;
        _liveStatus = liveStatus;
    }

    public async Task OpenAsync(
        TuiSession session,
        string? arg,
        Action clearConversationHistory,
        CancellationToken ct)
    {
        var result = await _openAction.ExecuteAsync(new OpenWorkspaceInput(arg), ct);
        if (!result.IsSuccess)
        {
            if (result.Failure.Reason == ActionFailureReason.Cancelled)
                return;

            // "Workspace 'foo' not found" when the user explicitly typed a name
            // is an error; "No workspaces registered" (returned when arg is
            // empty and the registry is empty) is just a state hint — preserve
            // the original UX writer split, since the action layer collapses
            // both into NotFound.
            if (result.Failure.Reason == ActionFailureReason.NotFound && string.IsNullOrWhiteSpace(arg))
                CliTheme.WriteWarning(result.Failure.Message);
            else
                CliTheme.WriteError(result.Failure.Message);
            return;
        }

        if (!session.TryOpen(result.Value.Name, out var error))
        {
            CliTheme.WriteError(error ?? "Failed to open workspace.");
            return;
        }

        clearConversationHistory();
        _agentSelector.TrySelectOnlyAgent(session);

        if (session.StateWarning is not null)
            CliTheme.WriteWarning(session.StateWarning);

        await PrintSummaryAsync(session, ct);
    }

    private async Task PrintSummaryAsync(TuiSession session, CancellationToken ct)
    {
        if (session.ManifestPath is null)
            return;

        WorkspaceManifest manifest;
        try
        {
            var json = await File.ReadAllTextAsync(session.ManifestPath, ct);
            manifest = _parser.Parse(json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException)
        {
            CliTheme.WriteError($"Failed to parse manifest: {ex.Message}");
            return;
        }

        CliTheme.WriteSection($"Workspace · {manifest.Name}");
        var liveRendered = await _liveStatus.TryRenderOnceAsync(session.ManifestPath, manifest, ct);
        if (!liveRendered)
            TuiManifestView.Render(manifest, session.ManifestPath);

        AnsiConsole.WriteLine();
        TuiNextStepHint.Render(session);
    }
}
