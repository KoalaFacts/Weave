using System.Globalization;
using Spectre.Console;
using Weave.Cli.Commands;

namespace Weave.Cli.Tui;

internal sealed class TuiSlashCommandDispatcher
{
    private readonly TuiWorkspaceDashboard _dashboard;
    private readonly TuiChatSession _chatSession;
    private readonly TuiAgentSelector _agentSelector;
    private readonly TuiAgentListView _agentListView;
    private readonly TuiWorkspaceOpener _workspaceOpener;
    private readonly TuiWorkspaceStarter _workspaceStarter;
    private readonly TuiWorkspaceWatcher _workspaceWatcher;

    public TuiSlashCommandDispatcher(
        TuiWorkspaceDashboard dashboard,
        TuiChatSession chatSession,
        TuiAgentSelector agentSelector,
        TuiAgentListView agentListView,
        TuiWorkspaceOpener workspaceOpener,
        TuiWorkspaceStarter workspaceStarter,
        TuiWorkspaceWatcher workspaceWatcher)
    {
        _dashboard = dashboard;
        _chatSession = chatSession;
        _agentSelector = agentSelector;
        _agentListView = agentListView;
        _workspaceOpener = workspaceOpener;
        _workspaceStarter = workspaceStarter;
        _workspaceWatcher = workspaceWatcher;
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
                RenderHelp();
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
                await RenderToolsAsync(session, ct);
                return TuiDispatchResult.Continue;

            case "tasks":
                await RenderTasksAsync(session, ct);
                return TuiDispatchResult.Continue;

            case "history":
                _chatSession.ShowHistory(session);
                return TuiDispatchResult.Continue;

            case "status":
                return await RunWorkspaceCommandAsync(
                    session,
                    options => new WorkspaceStatusCliCommand().ExecuteAsync(options, ct),
                    ct);

            case "validate":
                return await RunWorkspaceCommandAsync(
                    session,
                    options => new WorkspaceValidateCliCommand().ExecuteAsync(options, ct),
                    ct);

            case "ports":
                await new PortsCliCommand().ExecuteAsync(new NoCliOptions(), ct);
                return TuiDispatchResult.Continue;

            case "config":
                ShowConfig();
                return TuiDispatchResult.Continue;

            case "up":
                await _workspaceStarter.StartAsync(session, ct);
                return TuiDispatchResult.Continue;

            case "down":
                await TuiWorkspaceStopper.StopAsync(session, ct);
                return TuiDispatchResult.Continue;

            case "new":
            case "n":
                ShowNewWorkspaceHint();
                return TuiDispatchResult.Continue;

            case "presets":
            case "p":
                await new WorkspacePresetsCliCommand().ExecuteAsync(new NoCliOptions(), ct);
                return TuiDispatchResult.Continue;

            case "webui":
            case "web":
            case "w":
                await new WebUiCliCommand().ExecuteAsync(new WebUiOptions(), ct);
                return TuiDispatchResult.Continue;

            case "system":
            case "sys":
                await ShowSystemAsync(ct);
                return TuiDispatchResult.Continue;

            case "version":
            case "v":
                await new VersionCliCommand().ExecuteAsync(new NoCliOptions(), ct);
                return TuiDispatchResult.Continue;

            case "upgrade":
            case "update":
                await new UpgradeCliCommand().ExecuteAsync(new NoCliOptions(), ct);
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

    private static void RenderHelp()
    {
        CliTheme.WriteSection("Help");

        RenderHelpGroup("Getting started", new (string Cmd, string Aliases, string Desc)[]
        {
            ("/help", "/?, help", "Show this help"),
            ("/new", "/n", "Hints for creating a workspace"),
            ("/presets", "/p", "Built-in workspace presets"),
            ("/open <ws>", "/o", "Open a workspace for this session"),
        });

        RenderHelpGroup("Chat with an agent", new (string Cmd, string Aliases, string Desc)[]
        {
            ("(type anything)", "", "Sends to the current agent"),
            ("/agents", "", "List agents in the current workspace"),
            ("/use <agent>", "/agent, /a", "Switch the current agent"),
            ("/history", "", "Show recent conversation messages"),
        });

        RenderHelpGroup("Workspace", new (string Cmd, string Aliases, string Desc)[]
        {
            ("/up", "", "Start the current workspace"),
            ("/down", "", "Stop the current workspace"),
            ("/watch", "", "Live auto-refresh of the current workspace"),
            ("/tools", "", "List tools in the running workspace"),
            ("/tasks", "", "List tasks for the active agent"),
            ("/status", "", "Show workspace status"),
            ("/validate", "", "Validate the workspace manifest"),
        });

        RenderHelpGroup("Monitor", new (string Cmd, string Aliases, string Desc)[]
        {
            ("/refresh", "/r", "Re-render the dashboard"),
            ("/system", "/sys", "Silo + config info"),
            ("/webui", "/web, /w", "Open the web dashboard in a browser"),
            ("/ports", "", "Show port assignments"),
            ("/config", "", "View CLI configuration"),
        });

        RenderHelpGroup("About this CLI", new (string Cmd, string Aliases, string Desc)[]
        {
            ("/version", "/v", "Installed version + cached update info"),
            ("/upgrade", "/update", "Check NuGet for a newer release"),
        });

        RenderHelpGroup("Housekeeping", new (string Cmd, string Aliases, string Desc)[]
        {
            ("/clear", "/cls", "Clear the screen"),
            ("/quit", "/exit, /q, quit", "Exit the TUI"),
        });

        AnsiConsole.WriteLine();
        CliTheme.WriteMuted(
            "Tip: anything without a leading slash is sent to the current agent. " +
            "Unknown commands get a \"did you mean?\" suggestion.");
    }

    private static void RenderHelpGroup(string title, (string Cmd, string Aliases, string Desc)[] rows)
    {
        AnsiConsole.MarkupLine(
            $"[bold rgb({CliTheme.Accent.R},{CliTheme.Accent.G},{CliTheme.Accent.B})]  {Markup.Escape(title)}[/]");

        foreach (var (cmd, aliases, desc) in rows)
        {
            var cmdCell = $"[bold rgb({CliTheme.Primary.R},{CliTheme.Primary.G},{CliTheme.Primary.B})]{Markup.Escape(cmd),-20}[/]";
            var aliasCell = string.IsNullOrEmpty(aliases)
                ? new string(' ', 18)
                : $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]{Markup.Escape(aliases),-18}[/]";
            AnsiConsole.MarkupLine($"    {cmdCell}  {aliasCell}  {Markup.Escape(desc)}");
        }

        AnsiConsole.WriteLine();
    }

    private static async Task RenderToolsAsync(TuiSession session, CancellationToken ct)
    {
        if (!session.IsRunning)
        {
            CliTheme.WriteMuted("Workspace is not running. Start it with /up first.");
            return;
        }

        IReadOnlyList<ApiToolResponse> tools;
        try
        {
            using var client = new WorkspaceApiClient();
            tools = await client.GetToolsAsync(session.WorkspaceId!, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or IOException)
        {
            CliTheme.WriteError($"Failed to fetch tools: {ex.Message}");
            return;
        }

        if (tools.Count == 0)
        {
            CliTheme.WriteMuted("No tools registered in this workspace.");
            return;
        }

        var table = CliTheme.CreateTable($"Tools · {session.WorkspaceName}");
        table.AddColumn(CliTheme.StyledColumn("Name"));
        table.AddColumn(CliTheme.StyledColumn("Type"));
        table.AddColumn(CliTheme.StyledColumn("Status"));
        table.AddColumn(CliTheme.StyledColumn("Endpoint"));

        foreach (var tool in tools.OrderBy(t => t.ToolName, StringComparer.Ordinal))
        {
            table.AddRow(
                $"[bold white]{Markup.Escape(tool.ToolName)}[/]",
                Markup.Escape(tool.ToolType ?? "—"),
                TuiMarkup.ColorStatus(tool.Status),
                Markup.Escape(tool.Endpoint ?? "—"));
        }

        AnsiConsole.Write(table);
    }

    private static async Task RenderTasksAsync(TuiSession session, CancellationToken ct)
    {
        if (!session.IsRunning)
        {
            CliTheme.WriteMuted("Workspace is not running. Start it with /up first.");
            return;
        }

        if (session.AgentName is null)
        {
            CliTheme.WriteMuted("No agent selected. Use /use <agent> first.");
            return;
        }

        IReadOnlyList<ApiTaskResponse> tasks;
        try
        {
            using var client = new WorkspaceApiClient();
            tasks = await client.GetTasksAsync(session.WorkspaceId!, session.AgentName, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or IOException)
        {
            CliTheme.WriteError($"Failed to fetch tasks: {ex.Message}");
            return;
        }

        if (tasks.Count == 0)
        {
            CliTheme.WriteMuted($"No tasks for agent '{session.AgentName}'.");
            return;
        }

        var table = CliTheme.CreateTable($"Tasks · {session.AgentName}");
        table.AddColumn(CliTheme.StyledColumn("ID"));
        table.AddColumn(CliTheme.StyledColumn("Description"));
        table.AddColumn(CliTheme.StyledColumn("Status"));
        table.AddColumn(CliTheme.StyledColumn("Created"));

        foreach (var task in tasks)
        {
            table.AddRow(
                Markup.Escape(task.TaskId),
                Markup.Escape(task.Description),
                TuiMarkup.ColorStatus(task.Status),
                task.CreatedAt.ToString("g", CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);
    }

    private static void ShowConfig()
    {
        var config = CliConfigStore.Load();

        CliTheme.WriteSection("CLI Configuration");

        var table = CliTheme.CreateTable();
        table.AddColumn(CliTheme.StyledColumn("Key"));
        table.AddColumn(CliTheme.StyledColumn("Value"));
        table.AddRow("defaultPort", config.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        table.AddRow("storage", Markup.Escape(config.Storage));
        table.AddRow("authMode", Markup.Escape(config.AuthMode));
        table.AddRow("requireHttps", config.RequireHttps ? "true" : "false");
        table.AddRow("siloPath",
            string.IsNullOrWhiteSpace(config.SiloPath)
                ? $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})](auto-detect)[/]"
                : Markup.Escape(config.SiloPath));

        AnsiConsole.Write(table);
        CliTheme.WriteMuted("Change settings with: weave config set <key> <value>");
    }

    private static void ShowNewWorkspaceHint()
    {
        CliTheme.WriteSection("Create a new workspace");

        AnsiConsole.Write(CliTheme.CreatePanel(
            "Workspaces are created from the command line so the CLI can lay out "
            + "the folder structure (prompts, .weave/, data) and register with the registry.",
            "Why not inline?"));
        AnsiConsole.WriteLine();

        CliTheme.WriteInfo("Run:   weave workspace new <name>");
        CliTheme.WriteMuted("         weave workspace new <name> --preset <preset>");
        AnsiConsole.WriteLine();
        CliTheme.WriteMuted("Tip: run with no arguments for a guided flow.");
    }

    private static async Task ShowSystemAsync(CancellationToken cancellationToken)
    {
        CliTheme.WriteSection("System info");

        var config = CliConfigStore.Load();
        var reachable = await TuiRuntimeProbe.ProbeSiloAsync(cancellationToken);

        var table = CliTheme.CreateTable();
        table.AddColumn(CliTheme.StyledColumn("Key"));
        table.AddColumn(CliTheme.StyledColumn("Value"));
        table.AddRow("Silo API",
            reachable
                ? TuiMarkup.ColorTag(CliTheme.Success, $"online · http://localhost:{config.DefaultPort}")
                : TuiMarkup.ColorTag(CliTheme.Muted, $"offline · http://localhost:{config.DefaultPort}"));
        table.AddRow("Default port", config.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        table.AddRow("Storage", Markup.Escape(config.Storage));
        table.AddRow("Auth mode", Markup.Escape(config.AuthMode));
        table.AddRow("Require HTTPS", config.RequireHttps ? "true" : "false");
        table.AddRow("Silo path",
            string.IsNullOrWhiteSpace(config.SiloPath)
                ? TuiMarkup.ColorTag(CliTheme.Muted, "(auto-detect)")
                : Markup.Escape(config.SiloPath));
        table.AddRow("Weave home",
            Markup.Escape(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave")));

        AnsiConsole.Write(table);
    }
}
