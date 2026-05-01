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

    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var session = new TuiSession();

        AnsiConsole.Clear();
        CliTheme.WriteBanner();
        VersionInfo.KickOffRefreshIfStale();
        await Dashboard.RefreshAsync(cancellationToken);
        RenderWelcomeHint();

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
                RenderSlashHelp();
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
                ShowConfig();
                return DispatchResult.Continue;

            case "up":
                await StartWorkspaceInSessionAsync(session, ct);
                return DispatchResult.Continue;

            case "down":
                await StopWorkspaceInSessionAsync(session, ct);
                return DispatchResult.Continue;

            case "new":
            case "n":
                ShowNewWorkspaceHint();
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
                await ShowSystemScreenAsync(ct);
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
        RenderNextStepHint(session);
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

        RenderNextStepHint(session);
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

        await Dashboard.WatchLiveStatusAsync(session.ManifestPath!, manifest, ct);
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
        var liveRendered = await Dashboard.TryRenderLiveStatusOnceAsync(session.ManifestPath, manifest, ct);
        if (!liveRendered)
            Dashboard.RenderManifestView(manifest, session.ManifestPath);

        AnsiConsole.WriteLine();
        RenderNextStepHint(session);
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

    // ── /status, /validate, /ports, /config command handlers ──────

    private static async Task ShowWorkspaceStatusAsync(TuiSession session, CancellationToken ct)
    {
        if (!session.HasWorkspace)
        {
            CliTheme.WriteMuted("No workspace open. Try: /open <workspace>");
            return;
        }

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

        CliTheme.WriteSection($"Status · {manifest.Name}");
        var liveRendered = await Dashboard.TryRenderLiveStatusOnceAsync(session.ManifestPath, manifest, ct);
        if (!liveRendered)
            Dashboard.RenderManifestView(manifest, session.ManifestPath);
    }

    private static async Task ValidateManifestAsync(TuiSession session, CancellationToken ct)
    {
        if (!session.HasWorkspace)
        {
            CliTheme.WriteMuted("No workspace open. Try: /open <workspace>");
            return;
        }

        if (session.ManifestPath is null)
            return;

        try
        {
            var json = await File.ReadAllTextAsync(session.ManifestPath, ct);
            var manifest = Parser.Parse(json);
            var errors = Parser.Validate(manifest);

            if (errors.Count > 0)
            {
                CliTheme.WriteError("Configuration invalid:");
                foreach (var error in errors)
                    CliTheme.WriteMuted($"  - {error}");
                return;
            }

            CliTheme.WriteSuccess("Configuration valid.");
            CliTheme.WriteKeyValue("Name", manifest.Name);
            CliTheme.WriteKeyValue("Agents", manifest.Agents.Count.ToString(CultureInfo.InvariantCulture));
            CliTheme.WriteKeyValue("Tools", manifest.Tools.Count.ToString(CultureInfo.InvariantCulture));
            CliTheme.WriteKeyValue("Targets", manifest.Targets.Count.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            CliTheme.WriteError($"Configuration invalid: {ex.Message}");
        }
    }

    private static void ShowPorts()
    {
        var table = CliTheme.CreateTable("Port Assignments");
        table.AddColumn(CliTheme.StyledColumn("Port"));
        table.AddColumn(CliTheme.StyledColumn("Name"));
        table.AddColumn(CliTheme.StyledColumn("Description"));

        foreach (var (name, port, description) in WeavePorts.All)
        {
            table.AddRow(
                port.ToString(CultureInfo.InvariantCulture),
                name,
                description);
        }

        AnsiConsole.Write(table);

        var config = CliConfigStore.Load();
        if (config.DefaultPort != WeavePorts.SiloHttp)
        {
            AnsiConsole.WriteLine();
            CliTheme.WriteInfo($"Config override: defaultPort = {config.DefaultPort.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private static void ShowConfig()
    {
        var config = CliConfigStore.Load();

        CliTheme.WriteSection("CLI Configuration");

        var table = CliTheme.CreateTable();
        table.AddColumn(CliTheme.StyledColumn("Key"));
        table.AddColumn(CliTheme.StyledColumn("Value"));
        table.AddRow("defaultPort", config.DefaultPort.ToString(CultureInfo.InvariantCulture));
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

    private static void RenderNextStepHint(TuiSession session)
    {
        if (!session.HasWorkspace)
            return;

        if (!session.IsRunning)
        {
            AnsiConsole.MarkupLine(
                $"[bold rgb({CliTheme.Warning.R},{CliTheme.Warning.G},{CliTheme.Warning.B})]Next:[/] " +
                $"workspace is not running. Start it right here:");
            AnsiConsole.MarkupLine(
                $"  [rgb({CliTheme.Accent.R},{CliTheme.Accent.G},{CliTheme.Accent.B})]/up[/]   " +
                $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]# starts '{Markup.Escape(session.WorkspaceName!)}' via the running Silo[/]");
            return;
        }

        if (session.AgentName is null)
        {
            AnsiConsole.MarkupLine(
                $"[bold rgb({CliTheme.Primary.R},{CliTheme.Primary.G},{CliTheme.Primary.B})]Next:[/] " +
                $"pick an agent with " +
                $"[rgb({CliTheme.Accent.R},{CliTheme.Accent.G},{CliTheme.Accent.B})]/agents[/] or " +
                $"[rgb({CliTheme.Accent.R},{CliTheme.Accent.G},{CliTheme.Accent.B})]/use <name>[/].");
            return;
        }

        AnsiConsole.MarkupLine(
            $"[bold rgb({CliTheme.Success.R},{CliTheme.Success.G},{CliTheme.Success.B})]Ready.[/] " +
            $"Type any message to send it to " +
            $"[bold rgb({CliTheme.Accent.R},{CliTheme.Accent.G},{CliTheme.Accent.B})]{Markup.Escape(session.AgentName)}[/], " +
            $"or [rgb({CliTheme.Accent.R},{CliTheme.Accent.G},{CliTheme.Accent.B})]/agents[/] to switch.");
    }

    private static void ShowVersionScreen()
    {
        CliTheme.WriteSection("Version");

        var current = VersionInfo.Current();
        var cache = VersionInfo.LoadCache();

        var table = CliTheme.CreateTable();
        table.AddColumn(CliTheme.StyledColumn("Key"));
        table.AddColumn(CliTheme.StyledColumn("Value"));
        table.AddRow("Installed", $"[bold white]v{Markup.Escape(current)}[/]");

        if (cache is not null)
        {
            var newer = VersionInfo.IsNewer(cache.LatestVersion, current);
            var latestCell = newer
                ? $"[rgb({CliTheme.Warning.R},{CliTheme.Warning.G},{CliTheme.Warning.B})]v{Markup.Escape(cache.LatestVersion)} (newer)[/]"
                : $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]v{Markup.Escape(cache.LatestVersion)}[/]";
            table.AddRow("Latest (cached)", latestCell);
            table.AddRow("Last checked",
                cache.CheckedAt.ToLocalTime().ToString("u", System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            table.AddRow("Latest (cached)",
                $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]not checked yet[/]");
        }

        AnsiConsole.Write(table);
        CliTheme.WriteMuted("Run /upgrade to check NuGet now.");
    }

    private static async Task CheckForUpgradeAsync(CancellationToken ct)
    {
        CliTheme.WriteSection("Check for upgrade");

        UpdateCheckResult? result = null;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(CliTheme.AccentStyle)
            .StartAsync("Querying NuGet…", async _ =>
            {
                result = await VersionInfo.CheckAsync(ct);
            });

        if (result is null)
        {
            CliTheme.WriteError("Upgrade check returned no result.");
            return;
        }

        CliTheme.WriteKeyValue("Installed", $"v{result.Current}");
        if (result.Latest is not null)
            CliTheme.WriteKeyValue("Latest", $"v{result.Latest}");

        if (result.UpdateAvailable && result.Latest is not null)
        {
            AnsiConsole.MarkupLine(
                $"[rgb({CliTheme.Warning.R},{CliTheme.Warning.G},{CliTheme.Warning.B})]" +
                $"↑ v{Markup.Escape(result.Latest)} is available.[/]");
            CliTheme.WriteMuted($"  Upgrade:  {VersionInfo.UpgradeCommand}");
        }
        else if (result.Latest is not null)
        {
            CliTheme.WriteSuccess("You are on the latest version.");
        }
        else if (result.Note is not null)
        {
            CliTheme.WriteWarning(result.Note);
        }
    }

    private static void RenderWelcomeHint()
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

    private static void RenderSlashHelp()
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

    private static void ShowPresetsScreen()
    {
        CliTheme.WriteSection("Workspace presets");

        var table = CliTheme.CreateTable();
        table.AddColumn(CliTheme.StyledColumn("Preset"));
        table.AddColumn(CliTheme.StyledColumn("Description"));
        table.AddColumn(CliTheme.StyledColumn("Model"));
        table.AddColumn(CliTheme.StyledColumn("Tools"));

        foreach (var (name, preset) in WorkspacePresets.All)
        {
            var toolsCell = preset.Tools.Count > 0
                ? string.Join(", ", preset.Tools)
                : TuiMarkup.ColorTag(CliTheme.Muted, "none");

            table.AddRow(
                $"[bold]{Markup.Escape(name)}[/]",
                Markup.Escape(preset.Description),
                Markup.Escape(preset.Model),
                toolsCell);
        }

        AnsiConsole.Write(table);
        CliTheme.WriteMuted("Use: weave workspace new <name> --preset <preset>");
    }

    private static async Task ShowSystemScreenAsync(CancellationToken cancellationToken)
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
        table.AddRow("Default port", config.DefaultPort.ToString(CultureInfo.InvariantCulture));
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

    private static async Task OpenWebUiAsync(CancellationToken cancellationToken)
    {
        CliTheme.WriteSection("Web UI");

        var webUi = new WebUiRuntime();
        var url = webUi.DefaultUrl();
        CliTheme.WriteKeyValue("URL", url);

        var reachable = await webUi.IsReachableAsync(url, cancellationToken);
        if (reachable)
            CliTheme.WriteSuccess("Dashboard is reachable.");
        else
            CliTheme.WriteWarning("Dashboard is not reachable yet. Start it with `weave run` or the AppHost.");

        AnsiConsole.WriteLine();
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("What would you like to do?")
                .Styled()
                .AddChoices("Open in browser", "Copy URL (print)", "(cancel)"));

        switch (choice)
        {
            case "Open in browser":
                if (webUi.TryOpenBrowser(url))
                    CliTheme.WriteMuted("Opened in your default browser.");
                else
                    CliTheme.WriteMuted($"Could not open a browser automatically. Visit: {url}");
                break;

            case "Copy URL (print)":
                AnsiConsole.WriteLine(url);
                break;
        }
    }

}
