using Spectre.Console;
using Weave.Cli.Commands;

namespace Weave.Cli.Tui;

internal sealed class TuiShell
{
    private readonly TuiWorkspaceDashboard _dashboard = new();
    private readonly TuiWelcomeScreen _welcomeScreen = new();
    private readonly TuiChatSession _chatSession = new();
    private readonly TuiSlashCommandDispatcher _dispatcher;

    public TuiShell()
    {
        var liveStatus = new TuiLiveStatusView();
        var manifestView = new TuiManifestView();
        var nextStepHint = new TuiNextStepHint();
        var agentNameSource = new TuiAgentNameSource();
        var agentSelector = new TuiAgentSelector(agentNameSource, nextStepHint);

        _dispatcher = new TuiSlashCommandDispatcher(
            _dashboard,
            _chatSession,
            agentSelector,
            new TuiAgentListView(agentNameSource),
            new TuiWorkspaceOpener(agentSelector, liveStatus, manifestView, nextStepHint),
            new TuiWorkspaceStarter(agentSelector, nextStepHint),
            new TuiWorkspaceStopper(),
            new TuiWorkspaceWatcher(liveStatus),
            new TuiToolListView(),
            new TuiTaskListView());
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var session = new TuiSession();

        AnsiConsole.Clear();
        CliTheme.WriteBanner();
        VersionService.KickOffRefreshIfStale();
        await _dashboard.RefreshAsync(cancellationToken);
        _welcomeScreen.Render();

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

            if (IsSlashOrBareCommand(raw))
            {
                var body = raw.StartsWith('/') ? raw[1..] : raw;
                var result = await _dispatcher.DispatchAsync(session, body, cancellationToken);
                if (result == TuiDispatchResult.Quit)
                    break;
            }
            else
            {
                await _chatSession.SendAsync(session, raw, cancellationToken);
            }
        }

        AnsiConsole.MarkupLine(
            $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]See you next weave.[/]");
        return 0;
    }

    private static bool IsSlashOrBareCommand(string raw)
    {
        var (firstToken, _) = TuiCommandParser.Parse(raw);
        return raw.StartsWith('/') || TuiCommandParser.IsBareCommand(firstToken);
    }
}
