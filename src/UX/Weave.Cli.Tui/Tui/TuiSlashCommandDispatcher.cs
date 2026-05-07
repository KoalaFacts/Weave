using Spectre.Console;


namespace Weave.Cli.Tui;

internal sealed class TuiSlashCommandDispatcher
{
    private readonly TuiWorkspaceDashboard _dashboard;
    private readonly TuiChatSession _chatSession;
    private readonly TuiAgentSelector _agentSelector;
    private readonly TuiAgentListView _agentListView;
    private readonly TuiWorkspaceOpener _workspaceOpener;
    private readonly TuiWorkspaceStarter _workspaceStarter;
    private readonly TuiWorkspaceStopper _workspaceStopper;
    private readonly TuiWorkspaceWatcher _workspaceWatcher;
    private readonly TuiToolsView _toolsView;
    private readonly TuiTasksView _tasksView;
    private readonly TuiConfigView _configView;
    private readonly TuiSystemView _systemView;
    private readonly WorkspaceStatusCliCommand _statusCommand;
    private readonly WorkspaceValidateCliCommand _validateCommand;
    private readonly WebUiCliCommand _webUiCommand;
    private readonly UpgradeCliCommand _upgradeCommand;
    private readonly PortsCliCommand _portsCommand;

    public TuiSlashCommandDispatcher(
        TuiWorkspaceDashboard dashboard,
        TuiChatSession chatSession,
        TuiAgentSelector agentSelector,
        TuiAgentListView agentListView,
        TuiWorkspaceOpener workspaceOpener,
        TuiWorkspaceStarter workspaceStarter,
        TuiWorkspaceStopper workspaceStopper,
        TuiWorkspaceWatcher workspaceWatcher,
        TuiToolsView toolsView,
        TuiTasksView tasksView,
        TuiConfigView configView,
        TuiSystemView systemView,
        WorkspaceStatusCliCommand statusCommand,
        WorkspaceValidateCliCommand validateCommand,
        WebUiCliCommand webUiCommand,
        UpgradeCliCommand upgradeCommand,
        PortsCliCommand portsCommand)
    {
        _dashboard = dashboard;
        _chatSession = chatSession;
        _agentSelector = agentSelector;
        _agentListView = agentListView;
        _workspaceOpener = workspaceOpener;
        _workspaceStarter = workspaceStarter;
        _workspaceStopper = workspaceStopper;
        _workspaceWatcher = workspaceWatcher;
        _toolsView = toolsView;
        _tasksView = tasksView;
        _configView = configView;
        _systemView = systemView;
        _statusCommand = statusCommand;
        _validateCommand = validateCommand;
        _webUiCommand = webUiCommand;
        _upgradeCommand = upgradeCommand;
        _portsCommand = portsCommand;
    }

    public async Task<TuiDispatchResult> DispatchAsync(
        TuiSession session,
        string raw,
        CancellationToken ct)
    {
        var (name, args) = TuiCommandParser.Parse(raw);
        switch (name)
        {
            case "":
                return TuiDispatchResult.Continue;

            case "help":
            case "?":
                TuiHelpView.Render();
                return TuiDispatchResult.Continue;

            case "quit":
            case "exit":
            case "q":
                return TuiDispatchResult.Quit;

            case "clear":
            case "cls":
                AnsiConsole.Clear();
                CliTheme.WriteBanner();
                return TuiDispatchResult.Continue;

            case "refresh":
            case "r":
                await _dashboard.RefreshAsync(ct);
                return TuiDispatchResult.Continue;

            case "open":
            case "o":
                await _workspaceOpener.OpenAsync(session, args, _chatSession.Clear, ct);
                return TuiDispatchResult.Continue;

            case "use":
            case "agent":
            case "a":
                await _agentSelector.SelectAsync(session, args, _chatSession.Clear, ct);
                return TuiDispatchResult.Continue;

            case "agents":
                await _agentListView.RenderAsync(session, ct);
                return TuiDispatchResult.Continue;

            case "watch":
                await _workspaceWatcher.WatchAsync(session, ct);
                return TuiDispatchResult.Continue;

            case "tools":
                await _toolsView.RenderAsync(session, ct);
                return TuiDispatchResult.Continue;

            case "tasks":
                await _tasksView.RenderAsync(session, ct);
                return TuiDispatchResult.Continue;

            case "history":
                _chatSession.ShowHistory(session);
                return TuiDispatchResult.Continue;

            case "status":
                return await RunWorkspaceCommandAsync(
                    session,
                    options => _statusCommand.ExecuteAsync(options, ct),
                    ct);

            case "validate":
                return await RunWorkspaceCommandAsync(
                    session,
                    options => _validateCommand.ExecuteAsync(options, ct),
                    ct);

            case "ports":
                await _portsCommand.ExecuteAsync(new NoCliOptions(), ct);
                return TuiDispatchResult.Continue;

            case "config":
                await _configView.ShowAsync(ct);
                return TuiDispatchResult.Continue;

            case "up":
                await _workspaceStarter.StartAsync(session, ct);
                return TuiDispatchResult.Continue;

            case "down":
                await _workspaceStopper.StopAsync(session, ct);
                return TuiDispatchResult.Continue;

            case "new":
            case "n":
                TuiNewWorkspaceHint.Show();
                return TuiDispatchResult.Continue;

            case "presets":
            case "p":
                await new WorkspacePresetsCliCommand().ExecuteAsync(new NoCliOptions(), ct);
                return TuiDispatchResult.Continue;

            case "webui":
            case "web":
            case "w":
                await _webUiCommand.ExecuteAsync(new WebUiOptions(), ct);
                return TuiDispatchResult.Continue;

            case "system":
            case "sys":
                await _systemView.ShowAsync(ct);
                return TuiDispatchResult.Continue;

            case "version":
            case "v":
                await new VersionCliCommand().ExecuteAsync(new NoCliOptions(), ct);
                return TuiDispatchResult.Continue;

            case "upgrade":
            case "update":
                await _upgradeCommand.ExecuteAsync(new NoCliOptions(), ct);
                return TuiDispatchResult.Continue;

            default:
                WriteUnknownCommand(name);
                return TuiDispatchResult.Continue;
        }
    }

    private static async Task<TuiDispatchResult> RunWorkspaceCommandAsync(
        TuiSession session,
        Func<WorkspaceNameOptions, Task<int>> executeAsync,
        CancellationToken ct)
    {
        if (!session.HasWorkspace)
        {
            CliTheme.WriteMuted("No workspace open. Try: /open <workspace>");
            return TuiDispatchResult.Continue;
        }

        await executeAsync(new WorkspaceNameOptions(session.WorkspaceName));
        return TuiDispatchResult.Continue;
    }

    private static void WriteUnknownCommand(string name)
    {
        var suggestion = TuiCommandParser.Suggest(name);
        if (suggestion is not null)
            CliTheme.WriteError($"Unknown command: /{name}. Did you mean /{suggestion}?  Type /help for all commands.");
        else
            CliTheme.WriteError($"Unknown command: /{name}. Type /help for all commands.");
    }
}
