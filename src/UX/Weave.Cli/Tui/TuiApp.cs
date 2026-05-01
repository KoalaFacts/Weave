using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;
using Weave.Cli.Commands;
using Weave.Shared;
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
    private static readonly TuiScreens Screens = new();

    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var session = new TuiSession();

        AnsiConsole.Clear();
        CliTheme.WriteBanner();
        VersionInfo.KickOffRefreshIfStale();
        await Dashboard.RefreshAsync(cancellationToken);
        Screens.RenderWelcomeHint();

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
                Screens.RenderSlashHelp();
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
                await UseAgentAsync(session, args, ct);
                return DispatchResult.Continue;

            case "agents":
                await ListAgentsAsync(session, ct);
                return DispatchResult.Continue;

            case "watch":
                await WatchSessionAsync(session, ct);
                return DispatchResult.Continue;

            case "tools":
                await ListToolsAsync(session, ct);
                return DispatchResult.Continue;

            case "tasks":
                await ListTasksAsync(session, ct);
                return DispatchResult.Continue;

            case "history":
                ShowConversationHistory(session);
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
                Screens.ShowConfig();
                return DispatchResult.Continue;

            case "up":
                await StartWorkspaceInSessionAsync(session, ct);
                return DispatchResult.Continue;

            case "down":
                await StopWorkspaceInSessionAsync(session, ct);
                return DispatchResult.Continue;

            case "new":
            case "n":
                Screens.ShowNewWorkspaceHint();
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
                await Screens.ShowSystemAsync(ct);
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
        TryAutoSelectAgent(session);

        if (session.StateWarning is not null)
            CliTheme.WriteWarning(session.StateWarning);

        await PrintWorkspaceSummaryAsync(session, ct);
    }

    private static async Task UseAgentAsync(
        TuiSession session,
        string? arg,
        CancellationToken ct)
    {
        if (!session.HasWorkspace)
        {
            CliTheme.WriteMuted("No workspace open. Try: /open <workspace>");
            return;
        }

        var agents = await FetchAgentNamesAsync(session, ct);
        if (agents.Count == 0)
        {
            CliTheme.WriteWarning("No agents available for this workspace.");
            return;
        }

        string? target = arg;
        if (string.IsNullOrWhiteSpace(target))
        {
            target = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Use which agent?")
                    .Styled()
                    .AddChoices([.. agents, "(cancel)"]));

            if (target == "(cancel)")
                return;
        }

        var match = agents.FirstOrDefault(a => string.Equals(a, target, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            CliTheme.WriteError($"Agent '{target}' not found in '{session.WorkspaceName}'.");
            return;
        }

        session.AgentName = match;
        ConversationHistory.Clear();
        CliTheme.WriteMuted($"Agent set to '{match}'.");
        Screens.RenderNextStepHint(session);
    }

    private static async Task ListAgentsAsync(TuiSession session, CancellationToken ct)
    {
        if (!session.HasWorkspace)
        {
            CliTheme.WriteMuted("No workspace open. Try: /open <workspace>");
            return;
        }

        // Try live API first for rich status info.
        if (session.IsRunning)
        {
            try
            {
                using var client = new WorkspaceApiClient();
                if (await client.IsReachableAsync(ct))
                {
                    var live = await client.GetAgentsAsync(session.WorkspaceId!, ct);
                    if (live.Count > 0)
                    {
                        var table = CliTheme.CreateTable($"Agents · {session.WorkspaceName}");
                        table.AddColumn(CliTheme.StyledColumn(""));
                        table.AddColumn(CliTheme.StyledColumn("Name"));
                        table.AddColumn(CliTheme.StyledColumn("Status"));
                        table.AddColumn(CliTheme.StyledColumn("Model"));
                        table.AddColumn(CliTheme.StyledColumn("Tasks"));
                        table.AddColumn(CliTheme.StyledColumn("Tools"));

                        foreach (var agent in live.OrderBy(a => a.AgentName, StringComparer.Ordinal))
                        {
                            var marker = string.Equals(agent.AgentName, session.AgentName, StringComparison.Ordinal)
                                ? TuiMarkup.ColorTag(CliTheme.Primary, "●")
                                : " ";
                            table.AddRow(
                                marker,
                                $"[bold white]{Markup.Escape(agent.AgentName)}[/]",
                                TuiMarkup.ColorStatus(agent.Status),
                                Markup.Escape(agent.Model ?? "—"),
                                agent.ActiveTasks?.Count.ToString(CultureInfo.InvariantCulture) ?? "0",
                                agent.ConnectedTools?.Count.ToString(CultureInfo.InvariantCulture) ?? "0");
                        }

                        AnsiConsole.Write(table);
                        if (session.AgentName is null)
                            CliTheme.WriteMuted("Pick one with: /use <name>");
                        return;
                    }
                }
            }
            catch (HttpRequestException)
            {
                // Fall through to manifest-only view.
            }
        }

        // Manifest-only fallback (workspace not running or Silo unreachable).
        var names = await FetchAgentNamesAsync(session, ct);
        if (names.Count == 0)
        {
            CliTheme.WriteWarning("No agents available.");
            return;
        }

        var fallbackTable = CliTheme.CreateTable($"Agents · {session.WorkspaceName}");
        fallbackTable.AddColumn(CliTheme.StyledColumn(""));
        fallbackTable.AddColumn(CliTheme.StyledColumn("Name"));

        foreach (var name in names.OrderBy(n => n, StringComparer.Ordinal))
        {
            var marker = string.Equals(name, session.AgentName, StringComparison.Ordinal)
                ? TuiMarkup.ColorTag(CliTheme.Primary, "●")
                : " ";
            fallbackTable.AddRow(marker, $"[bold white]{Markup.Escape(name)}[/]");
        }

        AnsiConsole.Write(fallbackTable);
        if (session.AgentName is null)
            CliTheme.WriteMuted("Pick one with: /use <name>");
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
            TryAutoSelectAgent(session);

        Screens.RenderNextStepHint(session);
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

    private static void TryAutoSelectAgent(TuiSession session)
    {
        if (session.ManifestPath is null)
            return;

        try
        {
            var manifest = Parser.Parse(File.ReadAllText(session.ManifestPath));
            if (manifest.Agents is { Count: 1 } agents)
                session.AgentName = agents.Keys.First();
        }
        catch (Exception ex)
        {
            CliTheme.WriteMuted($"Could not auto-select agent ({ex.Message}).");
        }
    }

    private static async Task<List<string>> FetchAgentNamesAsync(TuiSession session, CancellationToken ct)
    {
        if (session.IsRunning)
        {
            try
            {
                using var client = new WorkspaceApiClient();
                if (await client.IsReachableAsync(ct))
                {
                    var live = await client.GetAgentsAsync(session.WorkspaceId!, ct);
                    return [.. live.Select(a => a.AgentName)];
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                CliTheme.WriteMuted($"Could not reach Silo for agent list ({ex.Message}). Falling back to manifest.");
            }
        }

        if (session.ManifestPath is null)
            return [];

        try
        {
            var manifest = Parser.Parse(File.ReadAllText(session.ManifestPath));
            return manifest.Agents is null ? [] : [.. manifest.Agents.Keys];
        }
        catch (Exception ex)
        {
            CliTheme.WriteMuted($"Could not read manifest for agent names ({ex.Message}).");
            return [];
        }
    }

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
        Screens.RenderNextStepHint(session);
    }

    // ── /tools, /tasks, /history command handlers ──────────────────

    private static async Task ListToolsAsync(TuiSession session, CancellationToken ct)
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
        catch (Exception ex)
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

    private static async Task ListTasksAsync(TuiSession session, CancellationToken ct)
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
        catch (Exception ex)
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

    private static void ShowConversationHistory(TuiSession session)
    {
        if (session.AgentName is null)
        {
            CliTheme.WriteMuted("No agent selected. Use /use <agent> first.");
            return;
        }

        if (ConversationHistory.Count == 0)
        {
            CliTheme.WriteMuted("No conversation history yet. Send a message first.");
            return;
        }

        CliTheme.WriteSection($"History · {session.AgentName}");
        foreach (var msg in ConversationHistory)
        {
            var role = msg.Role ?? "unknown";
            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                CliTheme.WriteUserEcho(msg.Content ?? "");
            else
                CliTheme.WriteAgentReply(session.AgentName, msg.Content ?? "");
        }
    }

}
