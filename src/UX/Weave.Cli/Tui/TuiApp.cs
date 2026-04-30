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
    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var session = new TuiSession();

        AnsiConsole.Clear();
        CliTheme.WriteBanner();
        VersionInfo.KickOffRefreshIfStale();
        await RefreshDashboardAsync(cancellationToken);
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
            var (firstToken, _) = ParseCommand(raw);
            var isBareCommand = !raw.StartsWith('/') && BarewordCommands.Contains(firstToken);

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

    /// <summary>
    /// Splits a slash-free command line (e.g. "use my-agent") into a
    /// lowercase name and an optional argument tail.
    /// </summary>
    internal static (string Name, string? Args) ParseCommand(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0)
            return (string.Empty, null);

        var space = s.IndexOf(' ');
        if (space < 0)
            return (s.ToLowerInvariant(), null);

        var name = s[..space].ToLowerInvariant();
        var args = s[(space + 1)..].Trim();
        return (name, args.Length == 0 ? null : args);
    }

    private enum DispatchResult { Continue, Quit }

    // Words accepted *without* a leading slash, so first-time users
    // who instinctively type "help" or "quit" get what they expect.
    private static readonly HashSet<string> BarewordCommands =
        new(StringComparer.OrdinalIgnoreCase) { "help", "?", "quit", "exit" };

    // Canonical list used for "did you mean?" suggestions on typos.
    private static readonly string[] KnownCommands =
    [
        "open", "use", "agent", "agents", "watch", "tools", "tasks",
        "history", "status", "validate", "ports", "config",
        "up", "down", "clear", "cls", "refresh", "new",
        "presets", "webui", "web", "system", "sys", "version",
        "upgrade", "update", "help", "quit", "exit"
    ];

    private static readonly ManifestParser Parser = new();

    internal static string? SuggestCommand(string typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
            return null;

        var prefix = KnownCommands.FirstOrDefault(
            c => c.StartsWith(typed, StringComparison.OrdinalIgnoreCase));
        if (prefix is not null)
            return prefix;

        // Fall back to nearest by Levenshtein distance ≤ 2.
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var c in KnownCommands)
        {
            var d = LevenshteinDistance(c, typed);
            if (d < bestDistance && d <= 2)
            {
                bestDistance = d;
                best = c;
            }
        }
        return best;
    }

    internal static int LevenshteinDistance(string a, string b)
    {
        var n = a.Length;
        var m = b.Length;
        if (n == 0)
            return m;
        if (m == 0)
            return n;

        var d = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++)
            d[i, 0] = i;
        for (var j = 0; j <= m; j++)
            d[0, j] = j;

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }
        return d[n, m];
    }

    // ── Slash dispatch ─────────────────────────────────────────────

    private static async Task<DispatchResult> HandleSlashAsync(
        TuiSession session,
        string raw,
        CancellationToken ct)
    {
        var (name, args) = ParseCommand(raw);
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
                await RefreshDashboardAsync(ct);
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
                var suggestion = SuggestCommand(name);
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
                                ? ColorTag(CliTheme.Primary, "●")
                                : " ";
                            table.AddRow(
                                marker,
                                $"[bold white]{Markup.Escape(agent.AgentName)}[/]",
                                ColorStatus(agent.Status),
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
                ? ColorTag(CliTheme.Primary, "●")
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

        await WatchLiveStatusAsync(session.ManifestPath!, manifest, ct);
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
        var liveRendered = await TryRenderLiveStatusOnceAsync(session.ManifestPath, manifest, ct);
        if (!liveRendered)
            RenderManifestView(manifest, session.ManifestPath);

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
                ColorStatus(tool.Status),
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
                ColorStatus(task.Status),
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
        var liveRendered = await TryRenderLiveStatusOnceAsync(session.ManifestPath, manifest, ct);
        if (!liveRendered)
            RenderManifestView(manifest, session.ManifestPath);
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

    private static async Task RefreshDashboardAsync(CancellationToken ct)
    {
        var workspaces = WorkspaceRegistry.GetAll()
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .ToArray();
        var siloReachable = await ProbeSiloAsync(ct);
        RenderDashboardStats(workspaces, siloReachable);
        RenderWorkspacesTable(workspaces);
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

    private static void RenderCompactHeader()
    {
        var rule = new Rule(
            $"[bold rgb({CliTheme.Primary.R},{CliTheme.Primary.G},{CliTheme.Primary.B})]◆ Weave[/] " +
            $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]· TUI[/]")
            .RuleStyle(CliTheme.MutedStyle)
            .LeftJustified();
        AnsiConsole.Write(rule);
    }

    private static void RenderDashboardStats(KeyValuePair<string, string>[] workspaces, bool siloReachable)
    {
        var totals = ComputeTotals(workspaces);

        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap())
            .AddColumn(new GridColumn().NoWrap())
            .AddColumn(new GridColumn().NoWrap())
            .AddColumn(new GridColumn().NoWrap());

        grid.AddRow(
            StatCard("Workspaces", totals.Total.ToString(CultureInfo.InvariantCulture), CliTheme.Primary),
            StatCard("Ready", totals.Ready.ToString(CultureInfo.InvariantCulture), CliTheme.Success),
            StatCard("Tracked runs", totals.Running.ToString(CultureInfo.InvariantCulture), CliTheme.Info),
            siloReachable
                ? StatCard("Silo", "online", CliTheme.Success, dot: "●")
                : StatCard("Silo", "offline", CliTheme.Muted, dot: "○"));

        AnsiConsole.Write(grid);
        AnsiConsole.WriteLine();
    }

    private static Panel StatCard(string label, string value, Color tint, string? dot = null)
    {
        var dotMarkup = dot is null
            ? string.Empty
            : $"[rgb({tint.R},{tint.G},{tint.B})]{dot}[/] ";

        var content =
            $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]{Markup.Escape(label)}[/]\n" +
            $"{dotMarkup}[bold rgb({tint.R},{tint.G},{tint.B})]{Markup.Escape(value)}[/]";

        return new Panel(new Markup(content))
            .Border(BoxBorder.Rounded)
            .BorderColor(CliTheme.Muted)
            .Padding(1, 0, 1, 0);
    }

    private static (int Total, int Ready, int Running) ComputeTotals(KeyValuePair<string, string>[] workspaces)
    {
        var ready = 0;
        var running = 0;

        foreach (var (_, dir) in workspaces)
        {
            var manifestPath = Path.Combine(dir, "workspace.json");
            if (File.Exists(manifestPath))
            {
                ready++;
                var statePath = WorkspaceApiClient.GetWorkspaceStatePath(manifestPath);
                if (File.Exists(statePath))
                    running++;
            }
        }

        return (workspaces.Length, ready, running);
    }

    private static void RenderWorkspacesTable(KeyValuePair<string, string>[] workspaces)
    {
        if (workspaces.Length == 0)
        {
            AnsiConsole.Write(CliTheme.CreatePanel(
                "No workspaces registered yet. Type /new to see how to create one.",
                "Workspaces"));
            AnsiConsole.WriteLine();
            return;
        }

        var table = CliTheme.CreateTable("Workspaces");
        table.AddColumn(CliTheme.StyledColumn("Name"));
        table.AddColumn(CliTheme.StyledColumn("Agents").RightAligned());
        table.AddColumn(CliTheme.StyledColumn("Tools").RightAligned());
        table.AddColumn(CliTheme.StyledColumn("Isolation"));
        table.AddColumn(CliTheme.StyledColumn("Manifest"));
        table.AddColumn(CliTheme.StyledColumn("Runtime"));

        foreach (var (name, dir) in workspaces)
        {
            var manifestPath = Path.Combine(dir, "workspace.json");
            var manifestOk = File.Exists(manifestPath);

            int agents = 0, tools = 0;
            var isolation = "—";

            if (manifestOk)
            {
                try
                {
                    var manifest = Parser.Parse(File.ReadAllText(manifestPath));
                    agents = manifest.Agents?.Count ?? 0;
                    tools = manifest.Tools?.Count ?? 0;
                    isolation = manifest.Workspace.Isolation.ToString().ToLowerInvariant();
                }
                catch (Exception)
                {
                    manifestOk = false;
                }
            }

            var statePath = manifestOk
                ? WorkspaceApiClient.GetWorkspaceStatePath(manifestPath)
                : null;
            var hasState = statePath is not null && File.Exists(statePath);

            var manifestCell = manifestOk
                ? ColorTag(CliTheme.Success, "● Ready")
                : ColorTag(CliTheme.Error, "✖ Missing");

            var runtimeCell = hasState
                ? ColorTag(CliTheme.Info, "● Tracked")
                : ColorTag(CliTheme.Muted, "○ Idle");

            table.AddRow(
                $"[bold white]{Markup.Escape(name)}[/]",
                agents.ToString(CultureInfo.InvariantCulture),
                tools.ToString(CultureInfo.InvariantCulture),
                Markup.Escape(isolation),
                manifestCell,
                runtimeCell);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static async Task<bool> TryRenderLiveStatusOnceAsync(
        string manifestPath,
        WorkspaceManifest manifest,
        CancellationToken cancellationToken)
    {
        var workspaceId = ReadWorkspaceId(manifestPath);
        if (workspaceId is null)
            return false;

        try
        {
            using var client = new WorkspaceApiClient();
            if (!await client.IsReachableAsync(cancellationToken))
                return false;

            var workspace = await client.GetWorkspaceAsync(workspaceId, cancellationToken);
            var agents = await client.GetAgentsAsync(workspaceId, cancellationToken);
            var tools = await client.GetToolsAsync(workspaceId, cancellationToken);

            AnsiConsole.Write(BuildLiveStatusRenderable(manifestPath, manifest, workspace, agents, tools));
            return true;
        }
        catch (Exception ex)
        {
            CliTheme.WriteWarning($"Live status unavailable: {ex.Message}. Showing manifest instead.");
            return false;
        }
    }

    private static async Task WatchLiveStatusAsync(
        string manifestPath,
        WorkspaceManifest manifest,
        CancellationToken cancellationToken)
    {
        var workspaceId = ReadWorkspaceId(manifestPath);
        if (workspaceId is null)
        {
            CliTheme.WriteWarning("No workspace ID on disk — nothing to watch.");
            return;
        }

        AnsiConsole.Clear();
        RenderCompactHeader();
        CliTheme.WriteSection($"Watching · {manifest.Name}");
        AnsiConsole.MarkupLine(
            $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]" +
            $"Auto-refresh every 2s · press any key to return[/]");
        AnsiConsole.WriteLine();

        var placeholder = new Markup(
            $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]Loading…[/]");

        using var client = new WorkspaceApiClient();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await AnsiConsole.Live(placeholder)
                .AutoClear(false)
                .StartAsync(async ctx =>
                {
                    while (!lifetime.IsCancellationRequested)
                    {
                        IRenderable next;
                        try
                        {
                            if (!await client.IsReachableAsync(lifetime.Token))
                            {
                                next = new Markup(
                                    $"[rgb({CliTheme.Warning.R},{CliTheme.Warning.G},{CliTheme.Warning.B})]" +
                                    $"Silo is not reachable. Retrying…[/]");
                            }
                            else
                            {
                                var workspace = await client.GetWorkspaceAsync(workspaceId, lifetime.Token);
                                var agents = await client.GetAgentsAsync(workspaceId, lifetime.Token);
                                var tools = await client.GetToolsAsync(workspaceId, lifetime.Token);
                                next = BuildLiveStatusRenderable(manifestPath, manifest, workspace, agents, tools);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            next = new Markup(
                                $"[rgb({CliTheme.Warning.R},{CliTheme.Warning.G},{CliTheme.Warning.B})]" +
                                $"Live status error: {Markup.Escape(ex.Message)}[/]");
                        }

                        ctx.UpdateTarget(next);

                        for (var i = 0; i < 20 && !lifetime.IsCancellationRequested; i++)
                        {
                            if (KeyPressed())
                            {
                                DrainKey();
                                await lifetime.CancelAsync();
                                break;
                            }
                            await Task.Delay(100, lifetime.Token);
                        }
                    }
                });
        }
        catch (OperationCanceledException)
        {
            // expected on exit
        }
    }

    private static Rows BuildLiveStatusRenderable(
        string manifestPath,
        WorkspaceManifest manifest,
        ApiWorkspaceResponse workspace,
        IReadOnlyList<ApiAgentResponse> agents,
        IReadOnlyList<ApiToolResponse> tools)
    {
        var summary = CliTheme.CreateTable("Live Status");
        summary.AddColumn(CliTheme.StyledColumn("Property"));
        summary.AddColumn(CliTheme.StyledColumn("Value"));
        summary.AddRow("Workspace", $"[bold white]{Markup.Escape(manifest.Name)}[/]");
        summary.AddRow("Workspace ID", Markup.Escape(workspace.WorkspaceId));
        summary.AddRow("Status", ColorStatus(workspace.Status));
        summary.AddRow("Containers", workspace.ContainerCount.ToString(CultureInfo.InvariantCulture));
        if (workspace.StartedAt is { } started)
            summary.AddRow("Started", started.ToLocalTime().ToString("u", CultureInfo.InvariantCulture));
        summary.AddRow("Manifest", Markup.Escape(manifestPath));
        summary.AddRow("Refreshed",
            DateTime.Now.ToString("T", CultureInfo.InvariantCulture));

        var rows = new List<IRenderable> { summary };

        if (agents.Count > 0)
        {
            var agentTable = CliTheme.CreateTable("Agents");
            agentTable.AddColumn(CliTheme.StyledColumn("Name"));
            agentTable.AddColumn(CliTheme.StyledColumn("Status"));
            agentTable.AddColumn(CliTheme.StyledColumn("Model"));
            agentTable.AddColumn(CliTheme.StyledColumn("Active Tasks"));
            agentTable.AddColumn(CliTheme.StyledColumn("Tools"));

            foreach (var agent in agents.OrderBy(a => a.AgentName, StringComparer.Ordinal))
            {
                var taskSummary = agent.ActiveTasks.Count == 0
                    ? "—"
                    : string.Join(", ", agent.ActiveTasks.Select(t => t.Description));

                agentTable.AddRow(
                    $"[bold white]{Markup.Escape(agent.AgentName)}[/]",
                    ColorStatus(agent.Status),
                    Markup.Escape(agent.Model ?? string.Empty),
                    Markup.Escape(taskSummary),
                    Markup.Escape(string.Join(", ", agent.ConnectedTools)));
            }

            rows.Add(agentTable);
        }

        if (tools.Count > 0)
        {
            var toolTable = CliTheme.CreateTable("Tools");
            toolTable.AddColumn(CliTheme.StyledColumn("Name"));
            toolTable.AddColumn(CliTheme.StyledColumn("Type"));
            toolTable.AddColumn(CliTheme.StyledColumn("Status"));

            foreach (var tool in tools.OrderBy(t => t.ToolName, StringComparer.Ordinal))
                toolTable.AddRow(
                    $"[bold white]{Markup.Escape(tool.ToolName)}[/]",
                    Markup.Escape(tool.ToolType),
                    ColorStatus(tool.Status));

            rows.Add(toolTable);
        }

        return new Rows(rows);
    }

    internal static string ColorStatus(string status)
    {
        var lower = status.ToLowerInvariant();
        var tint = lower switch
        {
            "running" or "active" or "connected" or "ready" or "healthy" => CliTheme.Success,
            "starting" or "connecting" or "pending" => CliTheme.Info,
            "stopped" or "idle" or "disconnected" => CliTheme.Muted,
            _ when lower.Contains("error", StringComparison.Ordinal)
                   || lower.Contains("fail", StringComparison.Ordinal) => CliTheme.Error,
            _ => CliTheme.Warning,
        };
        return ColorTag(tint, status);
    }

    internal static string ColorTag(Color tint, string text)
        => $"[rgb({tint.R},{tint.G},{tint.B})]{Markup.Escape(text)}[/]";

    private static void RenderManifestView(WorkspaceManifest manifest, string manifestPath)
    {
        var table = CliTheme.CreateTable("Manifest");
        table.AddColumn(CliTheme.StyledColumn("Property"));
        table.AddColumn(CliTheme.StyledColumn("Value"));
        table.AddRow("Workspace", $"[bold white]{Markup.Escape(manifest.Name)}[/]");
        table.AddRow("Version", Markup.Escape(manifest.Version));
        table.AddRow("Isolation", manifest.Workspace.Isolation.ToString());
        table.AddRow("Manifest", Markup.Escape(manifestPath));
        AnsiConsole.Write(table);

        if (manifest.Agents is { Count: > 0 })
        {
            var agentTable = CliTheme.CreateTable("Agents (from manifest)");
            agentTable.AddColumn(CliTheme.StyledColumn("Name"));
            agentTable.AddColumn(CliTheme.StyledColumn("Model"));
            agentTable.AddColumn(CliTheme.StyledColumn("Tools"));

            foreach (var (agentName, agent) in manifest.Agents)
            {
                agentTable.AddRow(
                    $"[bold white]{Markup.Escape(agentName)}[/]",
                    Markup.Escape(agent.Model),
                    Markup.Escape(string.Join(", ", agent.Tools)));
            }

            AnsiConsole.Write(agentTable);
        }

        if (manifest.Tools is { Count: > 0 })
        {
            var toolTable = CliTheme.CreateTable("Tools (from manifest)");
            toolTable.AddColumn(CliTheme.StyledColumn("Name"));
            toolTable.AddColumn(CliTheme.StyledColumn("Type"));

            foreach (var (toolName, tool) in manifest.Tools)
                toolTable.AddRow(
                    $"[bold white]{Markup.Escape(toolName)}[/]",
                    Markup.Escape(tool.Type));

            AnsiConsole.Write(toolTable);
        }
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
                : ColorTag(CliTheme.Muted, "none");

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
        var reachable = await ProbeSiloAsync(cancellationToken);

        var table = CliTheme.CreateTable();
        table.AddColumn(CliTheme.StyledColumn("Key"));
        table.AddColumn(CliTheme.StyledColumn("Value"));
        table.AddRow("Silo API",
            reachable
                ? ColorTag(CliTheme.Success, $"online · http://localhost:{config.DefaultPort}")
                : ColorTag(CliTheme.Muted, $"offline · http://localhost:{config.DefaultPort}"));
        table.AddRow("Default port", config.DefaultPort.ToString(CultureInfo.InvariantCulture));
        table.AddRow("Storage", Markup.Escape(config.Storage));
        table.AddRow("Auth mode", Markup.Escape(config.AuthMode));
        table.AddRow("Require HTTPS", config.RequireHttps ? "true" : "false");
        table.AddRow("Silo path",
            string.IsNullOrWhiteSpace(config.SiloPath)
                ? ColorTag(CliTheme.Muted, "(auto-detect)")
                : Markup.Escape(config.SiloPath));
        table.AddRow("Weave home",
            Markup.Escape(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave")));

        AnsiConsole.Write(table);
    }

    private static async Task OpenWebUiAsync(CancellationToken cancellationToken)
    {
        CliTheme.WriteSection("Web UI");

        var url = WebUiCommand.DefaultUrl();
        CliTheme.WriteKeyValue("URL", url);

        var reachable = await WebUiCommand.IsReachableAsync(url, cancellationToken);
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
                if (WebUiCommand.TryOpenBrowser(url))
                    CliTheme.WriteMuted("Opened in your default browser.");
                else
                    CliTheme.WriteMuted($"Could not open a browser automatically. Visit: {url}");
                break;

            case "Copy URL (print)":
                AnsiConsole.WriteLine(url);
                break;
        }
    }

    private static async Task<bool> ProbeSiloAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new WorkspaceApiClient();
            return await client.IsReachableAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    private static string? ReadWorkspaceId(string manifestPath)
    {
        var statePath = WorkspaceApiClient.GetWorkspaceStatePath(manifestPath);
        if (!File.Exists(statePath))
            return null;

        try
        {
            var id = File.ReadAllText(statePath).Trim();
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool KeyPressed()
    {
        try
        {
            return Console.KeyAvailable;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void DrainKey()
    {
        try
        {
            while (Console.KeyAvailable)
                _ = Console.ReadKey(intercept: true);
        }
        catch (InvalidOperationException)
        {
            // stdin redirected — nothing to drain
        }
    }
}
