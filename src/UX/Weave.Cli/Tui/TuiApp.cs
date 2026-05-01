using Spectre.Console;
using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Models;

namespace Weave.Cli.Tui;

/// <summary>
/// Interactive REPL over the CLI primitives. Persistent prompt at the
/// bottom of the terminal, slash commands for workspace actions,
/// free-form text is sent to the currently-selected agent.
/// </summary>
internal static class TuiApp
{
    private static readonly TuiWorkspaceDashboard Dashboard = new();
    private static readonly TuiLiveStatusView LiveStatus = new();
    private static readonly TuiManifestView ManifestView = new();
    private static readonly TuiWelcomeScreen WelcomeScreen = new();
    private static readonly TuiHelpScreen HelpScreen = new();
    private static readonly TuiConfigScreen ConfigScreen = new();
    private static readonly TuiNextStepHint NextStepHint = new();
    private static readonly TuiNewWorkspaceHint NewWorkspaceHint = new();
    private static readonly TuiSystemScreen SystemScreen = new();
    private static readonly TuiAgentNameSource AgentNameSource = new();
    private static readonly TuiAgentSelector AgentSelector = new(AgentNameSource, NextStepHint);
    private static readonly TuiAgentListView AgentListView = new(AgentNameSource);
    private static readonly TuiToolListView ToolListView = new();
    private static readonly TuiTaskListView TaskListView = new();
    private static readonly TuiConversationHistoryView ConversationHistoryView = new();

    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var session = new TuiSession();

        AnsiConsole.Clear();
        CliTheme.WriteBanner();
        VersionInfo.KickOffRefreshIfStale();
        await Dashboard.RefreshAsync(cancellationToken);
        WelcomeScreen.Render();

        var composer = new ChatComposer();

        while (!cancellationToken.IsCancellationRequested)
        {
            ComposerResult composed;
            try
            {
                composed = await composer.ReadAsync(session, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (composed.Status == ComposerStatus.Cancelled)
                break;

            var raw = composed.Text.Trim();
            if (raw.Length == 0)
                continue;

            // Bareword forgiveness: a new user typing `help` or `quit`
            // should work without the leading slash.
            var (firstToken, _) = TuiCommandParser.Parse(raw);
            var isBareCommand = !raw.StartsWith('/') && TuiCommandParser.IsBareCommand(firstToken);

            if (raw.StartsWith('/') || isBareCommand)
            {
                var body = raw.StartsWith('/') ? raw[1..] : raw;
                var result = await HandleSlashAsync(session, body, cancellationToken);
                if (result == DispatchResult.Quit)
                    break;
            }
            else
            {
                await HandleChatAsync(session, raw, cancellationToken);
            }
        }

        AnsiConsole.MarkupLine(
            $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]See you next weave.[/]");
        return 0;
    }

    // ── Input parsing ──────────────────────────────────────────────

    private enum DispatchResult { Continue, Quit }

    private static readonly ManifestParser Parser = new();

    // ── Slash dispatch ─────────────────────────────────────────────

    private static async Task<DispatchResult> HandleSlashAsync(
        TuiSession session,
        string raw,
        CancellationToken ct)
    {
        var (name, args) = TuiCommandParser.Parse(raw);
        switch (name)
        {
            case "":
                return DispatchResult.Continue;

            case "help":
            case "?":
                HelpScreen.Render();
                return DispatchResult.Continue;

            case "quit":
            case "exit":
            case "q":
                return DispatchResult.Quit;

            case "clear":
            case "cls":
                AnsiConsole.Clear();
                CliTheme.WriteBanner();
                return DispatchResult.Continue;

            case "refresh":
            case "r":
                await Dashboard.RefreshAsync(ct);
                return DispatchResult.Continue;

            case "open":
            case "o":
                await OpenWorkspaceAsync(session, args, ct);
                return DispatchResult.Continue;

            case "use":
            case "agent":
            case "a":
                await AgentSelector.SelectAsync(session, args, ConversationHistory.Clear, ct);
                return DispatchResult.Continue;

            case "agents":
                await AgentListView.RenderAsync(session, ct);
                return DispatchResult.Continue;

            case "watch":
                await WatchSessionAsync(session, ct);
                return DispatchResult.Continue;

            case "tools":
                await ToolListView.RenderAsync(session, ct);
                return DispatchResult.Continue;

            case "tasks":
                await TaskListView.RenderAsync(session, ct);
                return DispatchResult.Continue;

            case "history":
                ConversationHistoryView.Render(session, ConversationHistory);
                return DispatchResult.Continue;

            case "status":
                if (!session.HasWorkspace)
                {
                    CliTheme.WriteMuted("No workspace open. Try: /open <workspace>");
                    return DispatchResult.Continue;
                }
                await new WorkspaceStatusCliCommand().ExecuteAsync(new WorkspaceNameOptions(session.WorkspaceName), ct);
                return DispatchResult.Continue;

            case "validate":
                if (!session.HasWorkspace)
                {
                    CliTheme.WriteMuted("No workspace open. Try: /open <workspace>");
                    return DispatchResult.Continue;
                }
                await new WorkspaceValidateCliCommand().ExecuteAsync(new WorkspaceNameOptions(session.WorkspaceName), ct);
                return DispatchResult.Continue;

            case "ports":
                await new PortsCliCommand().ExecuteAsync(new NoCliOptions(), ct);
                return DispatchResult.Continue;

            case "config":
                ConfigScreen.Show();
                return DispatchResult.Continue;

            case "up":
                await StartWorkspaceInSessionAsync(session, ct);
                return DispatchResult.Continue;

            case "down":
                await StopWorkspaceInSessionAsync(session, ct);
                return DispatchResult.Continue;

            case "new":
            case "n":
                NewWorkspaceHint.Show();
                return DispatchResult.Continue;

            case "presets":
            case "p":
                await new WorkspacePresetsCliCommand().ExecuteAsync(new NoCliOptions(), ct);
                return DispatchResult.Continue;

            case "webui":
            case "web":
            case "w":
                await new WebUiCliCommand().ExecuteAsync(new WebUiOptions(), ct);
                return DispatchResult.Continue;

            case "system":
            case "sys":
                await SystemScreen.ShowAsync(ct);
                return DispatchResult.Continue;

            case "version":
            case "v":
                await new VersionCliCommand().ExecuteAsync(new NoCliOptions(), ct);
                return DispatchResult.Continue;

            case "upgrade":
            case "update":
                await new UpgradeCliCommand().ExecuteAsync(new NoCliOptions(), ct);
                return DispatchResult.Continue;

            default:
                var suggestion = TuiCommandParser.Suggest(name);
                if (suggestion is not null)
                    CliTheme.WriteError($"Unknown command: /{name}. Did you mean /{suggestion}?  Type /help for all commands.");
                else
                    CliTheme.WriteError($"Unknown command: /{name}. Type /help for all commands.");
                return DispatchResult.Continue;
        }
    }

    // ── Chat flow ──────────────────────────────────────────────────

    private static readonly List<ApiConversationMessage> ConversationHistory = [];

    private static async Task HandleChatAsync(
        TuiSession session,
        string message,
        CancellationToken ct)
    {
        if (!session.HasWorkspace)
        {
            CliTheme.WriteMuted("No workspace open. Try: /open <workspace>");
            return;
        }
        if (!session.IsRunning)
        {
            CliTheme.WriteMuted(
                $"Workspace '{session.WorkspaceName}' is not running. Type /up to start it.");
            return;
        }
        if (session.AgentName is null)
        {
            CliTheme.WriteMuted("No agent selected. Try: /agents, then /use <agent>");
            return;
        }

        CliTheme.WriteUserEcho(message);

        ApiChatResponse? reply = null;
        Exception? error = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(CliTheme.AccentStyle)
            .StartAsync($"{session.AgentName} is thinking…", async _ =>
            {
                try
                {
                    using var client = new WorkspaceApiClient();
                    reply = await client.SendAgentMessageAsync(
                        session.WorkspaceId!, session.AgentName, message, ct);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });

        if (error is not null)
        {
            CliTheme.WriteError($"Agent call failed: {error.Message}");
        }
        else if (reply is not null)
        {
            // Track conversation history.
            ConversationHistory.Add(new ApiConversationMessage { Role = "user", Content = message, Timestamp = DateTimeOffset.UtcNow });
            ConversationHistory.Add(new ApiConversationMessage { Role = "assistant", Content = reply.Content, Timestamp = DateTimeOffset.UtcNow });
            if (reply.Messages is { Count: > 0 })
            {
                // Replace with server-side history if provided.
                ConversationHistory.Clear();
                ConversationHistory.AddRange(reply.Messages);
            }

            // Show tool usage indicator.
            if (reply.UsedTools)
            {
                CliTheme.WriteMuted("  Tools were used to generate this response.");
            }

            CliTheme.WriteAgentReply(session.AgentName, reply.Content);

            // Show model info.
            if (!string.IsNullOrWhiteSpace(reply.Model))
                CliTheme.WriteMuted($"  Model: {reply.Model}");
        }
    }

    // ── Command handlers ──────────────────────────────────────────

    private static async Task OpenWorkspaceAsync(
        TuiSession session,
        string? arg,
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

        ConversationHistory.Clear();
        AgentSelector.TrySelectOnlyAgent(session);

        if (session.StateWarning is not null)
            CliTheme.WriteWarning(session.StateWarning);

        await PrintWorkspaceSummaryAsync(session, ct);
    }

    private static async Task StartWorkspaceInSessionAsync(TuiSession session, CancellationToken ct)
    {
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
            manifest = WorkspaceApiClient.PrepareManifest(
                Parser.Parse(json),
                Path.GetDirectoryName(Path.GetFullPath(session.ManifestPath!))
                    ?? Directory.GetCurrentDirectory());
        }
        catch (Exception ex)
        {
            CliTheme.WriteError($"Failed to read manifest: {ex.Message}");
            return;
        }

        ApiWorkspaceResponse? response = null;
        Exception? error = null;
        WorkspaceSiloStarter.AutoStartResult? siloFailure = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(CliTheme.AccentStyle)
            .StartAsync($"Starting '{manifest.Name}'…", async ctx =>
            {
                try
                {
                    using var client = new WorkspaceApiClient();

                    if (!await client.IsReachableAsync(ct))
                    {
                        var siloPath = WorkspaceSiloStarter.ResolveSiloPath();
                        if (siloPath is null)
                        {
                            error = new InvalidOperationException(
                                "Could not locate the Weave Silo on disk. " +
                                "Set WEAVE_SILO_PATH, run `weave config set silo-path <path>`, " +
                                "or start the TUI from the repo root.");
                            return;
                        }

                        ctx.Status($"Silo not running — launching from {siloPath}…");
                        var outcome = await WorkspaceSiloStarter.AutoStartServeWithDiagnosticsAsync(ct);
                        if (!outcome.Success)
                        {
                            siloFailure = outcome;
                            return;
                        }
                        ctx.Status($"Silo ready — starting '{manifest.Name}'…");
                    }

                    response = await client.StartWorkspaceAsync(manifest, ct);

                    var statePath = WorkspaceApiClient.GetWorkspaceStatePath(session.ManifestPath!);
                    Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
                    await File.WriteAllTextAsync(statePath, response.WorkspaceId, ct);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });

        if (siloFailure is not null)
        {
            CliTheme.WriteError($"Silo start failed: {siloFailure.Reason ?? "unknown"}");
            CliTheme.WriteMuted($"  Log: {siloFailure.LogPath}");
            RenderLogTail(siloFailure.LogPath, lineCount: 15);
            return;
        }
        if (error is not null)
        {
            CliTheme.WriteError($"Failed to start: {error.Message}");
            return;
        }
        if (response is null)
            return;

        session.MarkRunning(response.WorkspaceId);
        CliTheme.WriteSuccess($"Workspace '{manifest.Name}' started.");
        CliTheme.WriteKeyValue("Workspace ID", response.WorkspaceId);
        CliTheme.WriteKeyValue("Status", response.Status);

        if (session.AgentName is null)
            AgentSelector.TrySelectOnlyAgent(session);

        NextStepHint.Render(session);
    }

    /// <summary>
    /// Prints the last <paramref name="lineCount"/> lines of the given
    /// log file inside a muted panel so the user sees the Silo crash
    /// reason without leaving the TUI.
    /// </summary>
    private static void RenderLogTail(string logPath, int lineCount)
    {
        if (!File.Exists(logPath))
            return;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(logPath);
        }
        catch (Exception ex)
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

    private static async Task StopWorkspaceInSessionAsync(TuiSession session, CancellationToken ct)
    {
        if (!session.IsRunning)
        {
            CliTheme.WriteMuted("Workspace is not running.");
            return;
        }

        var command = new WorkspaceDownCliCommand();
        var exitCode = await command.ExecuteAsync(
            new WorkspaceDownOptions(session.WorkspaceName, session.ManifestPath, session.WorkspaceId),
            ct);

        if (exitCode == 0)
            session.MarkStopped();
    }

    private static async Task WatchSessionAsync(TuiSession session, CancellationToken ct)
    {
        if (!session.HasWorkspace)
        {
            CliTheme.WriteMuted("No workspace open. Try: /open <workspace>");
            return;
        }
        if (!session.IsRunning)
        {
            CliTheme.WriteMuted($"Workspace '{session.WorkspaceName}' is not running.");
            return;
        }

        WorkspaceManifest manifest;
        try
        {
            var json = await File.ReadAllTextAsync(session.ManifestPath!, ct);
            manifest = Parser.Parse(json);
        }
        catch (Exception ex)
        {
            CliTheme.WriteError($"Failed to parse manifest: {ex.Message}");
            return;
        }

        await LiveStatus.WatchAsync(session.ManifestPath!, manifest, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static async Task PrintWorkspaceSummaryAsync(TuiSession session, CancellationToken ct)
    {
        if (session.ManifestPath is null)
            return;

        WorkspaceManifest manifest;
        try
        {
            var json = await File.ReadAllTextAsync(session.ManifestPath, ct);
            manifest = Parser.Parse(json);
        }
        catch (Exception ex)
        {
            CliTheme.WriteError($"Failed to parse manifest: {ex.Message}");
            return;
        }

        CliTheme.WriteSection($"Workspace · {manifest.Name}");
        var liveRendered = await LiveStatus.TryRenderOnceAsync(session.ManifestPath, manifest, ct);
        if (!liveRendered)
            ManifestView.Render(manifest, session.ManifestPath);

        AnsiConsole.WriteLine();
        NextStepHint.Render(session);
    }

}
