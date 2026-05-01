using Spectre.Console;
using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Models;

namespace Weave.Cli.Tui;

internal sealed class TuiWorkspaceOpener
{
    private readonly ManifestParser _parser = new();
    private readonly TuiAgentSelector _agentSelector;
    private readonly TuiLiveStatusView _liveStatus;
    private readonly TuiManifestView _manifestView;
    private readonly TuiNextStepHint _nextStepHint;

    public TuiWorkspaceOpener(
        TuiAgentSelector agentSelector,
        TuiLiveStatusView liveStatus,
        TuiManifestView manifestView,
        TuiNextStepHint nextStepHint)
    {
        _agentSelector = agentSelector;
        _liveStatus = liveStatus;
        _manifestView = manifestView;
        _nextStepHint = nextStepHint;
    }

    public async Task OpenAsync(
        TuiSession session,
        string? arg,
        Action clearConversationHistory,
        CancellationToken ct)
    {
        var workspaces = WorkspaceRegistry.GetAll();
        if (workspaces.Count == 0)
        {
            CliTheme.WriteWarning("No workspaces registered. Try /new for hints.");
            return;
        }

        var target = arg;
        if (string.IsNullOrWhiteSpace(target))
        {
            target = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Open which workspace?")
                    .Styled()
                    .AddChoices([.. workspaces.Keys, "(cancel)"]));

            if (target == "(cancel)")
                return;
        }

        if (!workspaces.ContainsKey(target))
        {
            var match = workspaces.Keys
                .FirstOrDefault(k => string.Equals(k, target, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                CliTheme.WriteError($"Workspace '{target}' not found.");
                return;
            }
            target = match;
        }

        if (!session.TryOpen(target, out var error))
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
        catch (Exception ex)
        {
            CliTheme.WriteError($"Failed to parse manifest: {ex.Message}");
            return;
        }

        CliTheme.WriteSection($"Workspace · {manifest.Name}");
        var liveRendered = await _liveStatus.TryRenderOnceAsync(session.ManifestPath, manifest, ct);
        if (!liveRendered)
            _manifestView.Render(manifest, session.ManifestPath);

        AnsiConsole.WriteLine();
        _nextStepHint.Render(session);
    }
}
