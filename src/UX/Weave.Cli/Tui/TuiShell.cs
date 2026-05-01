using Spectre.Console;
using Weave.Cli.Commands;

namespace Weave.Cli.Tui;

internal sealed class TuiShell
{
    private readonly TuiWorkspaceDashboard _dashboard = new();
    private readonly TuiChatSession _chatSession = new();
    private readonly TuiSlashCommandDispatcher _dispatcher;

    public TuiShell()
    {
        var agentNameSource = new TuiAgentNameSource();
        var agentSelector = new TuiAgentSelector(agentNameSource);

        _dispatcher = new TuiSlashCommandDispatcher(
            _dashboard,
            _chatSession,
            agentSelector,
            new TuiAgentListView(agentNameSource),
            new TuiWorkspaceOpener(agentSelector),
            new TuiWorkspaceStarter(agentSelector),
                new TuiWorkspaceWatcher());
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var session = new TuiSession();

        AnsiConsole.Clear();
        CliTheme.WriteBanner();
        VersionService.KickOffRefreshIfStale();
        await _dashboard.RefreshAsync(cancellationToken);
        RenderWelcome();

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

    private static void RenderWelcome()
    {
        var workspaceCount = WorkspaceRegistry.GetAll().Count;

        if (workspaceCount == 0)
        {
            var content =
                $"[bold white]1.[/] Create a workspace\n" +
                $"   [rgb({CliTheme.Accent.R},{CliTheme.Accent.G},{CliTheme.Accent.B})]weave workspace new my-first[/]\n" +
                $"   [rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]or type /new here for guidance, /presets for samples[/]\n\n" +
                $"[bold white]2.[/] Start it\n" +
                $"   [rgb({CliTheme.Accent.R},{CliTheme.Accent.G},{CliTheme.Accent.B})]weave up my-first[/]\n\n" +
                $"[bold white]3.[/] Come back here and chat\n" +
                $"   [rgb({CliTheme.Accent.R},{CliTheme.Accent.G},{CliTheme.Accent.B})]/open my-first[/] " +
                $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]→[/] " +
                $"[rgb({CliTheme.Accent.R},{CliTheme.Accent.G},{CliTheme.Accent.B})]/use <agent>[/] " +
                $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]→ type any message[/]";

            AnsiConsole.Write(CliTheme.CreatePanel(content, "Getting started"));
            AnsiConsole.WriteLine();
            CliTheme.WriteMuted("Type /help anytime to see every command grouped by task. Ctrl+C to exit.");
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]" +
                $"Start with [bold]/open <workspace>[/], or type [bold]/help[/] to see commands.  " +
                $"Ctrl+C to exit.[/]");
        }

        AnsiConsole.WriteLine();
    }
}
