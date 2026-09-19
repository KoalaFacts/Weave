using Spectre.Console;
using Weave.Actions.Context;
using Weave.Actions.SystemInfo;
using Weave.Actions.Workspace;
using Weave.Cli.Tui.Verbs;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tui;

internal sealed class TuiWorkspaceStarter(
    TuiAgentSelector agentSelector,
    StartWorkspaceAction startAction,
    GetSystemInfoAction systemInfoAction,
    ISiloLauncher siloLauncher) : ITuiVerb
{
    private readonly ManifestParser _parser = new();

    public string Name => "up";

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
            CliTheme.WriteMuted($"Workspace '{session.WorkspaceName}' is already running.");
            return;
        }

        WorkspaceManifest manifest;
        try
        {
            var json = await File.ReadAllTextAsync(session.ManifestPath!, ct);
            manifest = WorkspaceManifestPaths.PrepareForSilo(
                _parser.Parse(json),
                Path.GetDirectoryName(Path.GetFullPath(session.ManifestPath!))
                    ?? Directory.GetCurrentDirectory());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException or ArgumentException)
        {
            CliTheme.WriteError($"Failed to read manifest: {ex.Message}");
            return;
        }

        ActionResult<StartWorkspaceResult> startResult = default;
        SiloAutoStartResult? siloFailure = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(CliTheme.AccentStyle)
            .StartAsync($"Starting '{manifest.Name}'…", async ctx =>
            {
                var systemInfo = await systemInfoAction.ExecuteAsync(new GetSystemInfoInput(), ct);
                if (systemInfo.IsSuccess && !systemInfo.Value.Reachable)
                {
                    var siloPath = siloLauncher.ResolveSiloPath();
                    if (siloPath is null)
                    {
                        startResult = ActionResult.Failed<StartWorkspaceResult>(ActionFailure.Internal(
                            "Could not locate the Weave Silo on disk. " +
                            "Set WEAVE_SILO_PATH, run `weave config set silo-path <path>`, " +
                            "or start the TUI from the repo root."));
                        return;
                    }

                    ctx.Status($"Silo not running — launching from {siloPath}…");
                    var outcome = await siloLauncher.AutoStartServeWithDiagnosticsAsync(ct);
                    if (!outcome.Success)
                    {
                        siloFailure = outcome;
                        return;
                    }
                    ctx.Status($"Silo ready — starting '{manifest.Name}'…");
                }

                startResult = await startAction.ExecuteAsync(new StartWorkspaceInput(manifest), ct);

                if (startResult.IsSuccess)
                {
                    var statePath = WorkspaceManifestPaths.GetStatePath(session.ManifestPath!);
                    Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
                    await File.WriteAllTextAsync(statePath, startResult.Value.Workspace.WorkspaceId, ct);
                }
            });

        if (siloFailure is not null)
        {
            CliTheme.WriteError($"Silo start failed: {siloFailure.Reason ?? "unknown"}");
            CliTheme.WriteMuted($"  Log: {siloFailure.LogPath}");
            RenderLogTail(siloFailure.LogPath, lineCount: 15);
            return;
        }
        if (!startResult.IsSuccess)
        {
            CliTheme.WriteError($"Failed to start: {startResult.Failure.Message}");
            return;
        }

        var workspace = startResult.Value.Workspace;
        session.MarkRunning(workspace.WorkspaceId);
        CliTheme.WriteSuccess($"Workspace '{manifest.Name}' started.");
        CliTheme.WriteKeyValue("Workspace ID", workspace.WorkspaceId);
        CliTheme.WriteKeyValue("Status", workspace.Status);

        if (session.AgentName is null)
            agentSelector.TrySelectOnlyAgent(session);

        TuiNextStepHint.Render(session);
    }

    private static void RenderLogTail(string logPath, int lineCount)
    {
        if (!File.Exists(logPath))
            return;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(logPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CliTheme.WriteMuted($"  (could not read log: {ex.Message})");
            return;
        }

        var tail = lines.Length <= lineCount
            ? lines
            : lines[^lineCount..];

        var body = string.Join(
            '\n',
            tail.Select(l => $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]{Markup.Escape(l)}[/]"));

        AnsiConsole.Write(CliTheme.CreatePanel(body, $"last {tail.Length} lines of silo.log"));
    }
}
