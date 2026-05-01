using Spectre.Console;
using Weave.Cli.Commands;

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
    private static readonly TuiWorkspaceOpener WorkspaceOpener = new(AgentSelector, LiveStatus, ManifestView, NextStepHint);
    private static readonly TuiWorkspaceStarter WorkspaceStarter = new(AgentSelector, NextStepHint);
    private static readonly TuiWorkspaceStopper WorkspaceStopper = new();
    private static readonly TuiWorkspaceWatcher WorkspaceWatcher = new(LiveStatus);
    private static readonly TuiToolListView ToolListView = new();
    private static readonly TuiTaskListView TaskListView = new();
    private static readonly TuiChatSession ChatSession = new();

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
                await ChatSession.SendAsync(session, raw, cancellationToken);
            }
        }

        AnsiConsole.MarkupLine(
            $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]See you next weave.[/]");
        return 0;
    }

    // ── Input parsing ──────────────────────────────────────────────

    private enum DispatchResult { Continue, Quit }

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
                await WorkspaceOpener.OpenAsync(session, args, ChatSession.Clear, ct);
                return DispatchResult.Continue;

            case "use":
            case "agent":
            case "a":
                await AgentSelector.SelectAsync(session, args, ChatSession.Clear, ct);
                return DispatchResult.Continue;

            case "agents":
                await AgentListView.RenderAsync(session, ct);
                return DispatchResult.Continue;

            case "watch":
                await WorkspaceWatcher.WatchAsync(session, ct);
                return DispatchResult.Continue;

            case "tools":
                await ToolListView.RenderAsync(session, ct);
                return DispatchResult.Continue;

            case "tasks":
                await TaskListView.RenderAsync(session, ct);
                return DispatchResult.Continue;

            case "history":
                ChatSession.ShowHistory(session);
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
                await WorkspaceStarter.StartAsync(session, ct);
                return DispatchResult.Continue;

            case "down":
                await WorkspaceStopper.StopAsync(session, ct);
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

}
