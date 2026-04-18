using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;
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
    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var session = new TuiSession();

        AnsiConsole.Clear();
        CliTheme.WriteBanner();
        await RefreshDashboardAsync(cancellationToken);
        RenderWelcomeHint();

        while (!cancellationToken.IsCancellationRequested)
        {
            string input;
            try
            {
                var prompt = new TextPrompt<string>(CliTheme.PromptPrefix(session.WorkspaceName, session.AgentName))
                    .Styled()
                    .AllowEmpty();
                input = AnsiConsole.Prompt(prompt);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var raw = (input ?? string.Empty).Trim();
            if (raw.Length == 0)
                continue;

            if (raw.StartsWith('/'))
            {
                var result = await HandleSlashAsync(session, raw[1..], cancellationToken);
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

            case "new":
            case "n":
                ShowNewWorkspaceHint();
                return DispatchResult.Continue;

            case "presets":
            case "p":
                ShowPresetsScreen();
                return DispatchResult.Continue;

            case "webui":
            case "web":
            case "w":
                await OpenWebUiAsync(ct);
                return DispatchResult.Continue;

            case "system":
            case "sys":
                await ShowSystemScreenAsync(ct);
                return DispatchResult.Continue;

            default:
                CliTheme.WriteError($"Unknown command: /{name}. Try /help.");
                return DispatchResult.Continue;
        }
    }

    // ── Chat flow ──────────────────────────────────────────────────

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
                $"Workspace '{session.WorkspaceName}' is not running. " +
                $"Start it from another terminal with: weave up {session.WorkspaceName}");
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
            CliTheme.WriteError($"Agent call failed: {error.Message}");
        else if (reply is not null)
            CliTheme.WriteAgentReply(session.AgentName, reply.Content);
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

        TryAutoSelectAgent(session);
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
        CliTheme.WriteMuted($"Agent set to '{match}'.");
    }

    private static async Task ListAgentsAsync(TuiSession session, CancellationToken ct)
    {
        if (!session.HasWorkspace)
        {
            CliTheme.WriteMuted("No workspace open. Try: /open <workspace>");
            return;
        }

        var names = await FetchAgentNamesAsync(session, ct);
        if (names.Count == 0)
        {
            CliTheme.WriteWarning("No agents available.");
            return;
        }

        var table = CliTheme.CreateTable($"Agents · {session.WorkspaceName}");
        table.AddColumn(CliTheme.StyledColumn(""));
        table.AddColumn(CliTheme.StyledColumn("Name"));

        foreach (var name in names.OrderBy(n => n, StringComparer.Ordinal))
        {
            var marker = string.Equals(name, session.AgentName, StringComparison.Ordinal)
                ? ColorTag(CliTheme.Primary, "●")
                : " ";
            table.AddRow(marker, $"[bold white]{Markup.Escape(name)}[/]");
        }

        AnsiConsole.Write(table);
        if (session.AgentName is null)
            CliTheme.WriteMuted("Pick one with: /use <name>");
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
            manifest = new ManifestParser().Parse(json);
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
            var manifest = new ManifestParser().Parse(File.ReadAllText(session.ManifestPath));
            if (manifest.Agents is { Count: 1 } agents)
                session.AgentName = agents.Keys.First();
        }
        catch
        {
            // best-effort — /use still available
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
            catch
            {
                // fall through to manifest
            }
        }

        if (session.ManifestPath is null)
            return [];

        try
        {
            var manifest = new ManifestParser().Parse(File.ReadAllText(session.ManifestPath));
            return manifest.Agents is null ? [] : [.. manifest.Agents.Keys];
        }
        catch
        {
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
            manifest = new ManifestParser().Parse(json);
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

        if (session.AgentName is not null)
            CliTheme.WriteMuted(
                $"Current agent: {session.AgentName}  (type a message, or /use <name> to switch)");
        else
            CliTheme.WriteMuted("No agent selected. Try: /agents, then /use <agent>");
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
        AnsiConsole.MarkupLine(
            $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]" +
            $"Type a message to chat with the current agent, or [bold]/help[/] for commands.  " +
            $"Ctrl+C to exit.[/]");
        AnsiConsole.WriteLine();
    }

    private static void RenderSlashHelp()
    {
        var table = CliTheme.CreateTable("Slash commands");
        table.AddColumn(CliTheme.StyledColumn("Command"));
        table.AddColumn(CliTheme.StyledColumn("Aliases"));
        table.AddColumn(CliTheme.StyledColumn("Description"));

        table.AddRow("/open [name]", "/o", "Open a workspace for this session.");
        table.AddRow("/use [agent]", "/agent /a", "Pick the agent that receives free-form messages.");
        table.AddRow("/agents", "", "List agents in the current workspace.");
        table.AddRow("/watch", "", "Live-refresh the current workspace status.");
        table.AddRow("/refresh", "/r", "Re-render the dashboard.");
        table.AddRow("/clear", "/cls", "Clear the screen.");
        table.AddRow("/new", "/n", "Show hints for creating a workspace.");
        table.AddRow("/presets", "/p", "List built-in presets.");
        table.AddRow("/webui", "/web /w", "Open the web dashboard.");
        table.AddRow("/system", "/sys", "Show system and silo info.");
        table.AddRow("/help", "/?", "Show this help.");
        table.AddRow("/quit", "/exit /q", "Exit the TUI.");

        AnsiConsole.Write(table);
        CliTheme.WriteMuted("Or just type a message to send it to the current agent.");
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
                    var manifest = new ManifestParser().Parse(File.ReadAllText(manifestPath));
                    agents = manifest.Agents?.Count ?? 0;
                    tools = manifest.Tools?.Count ?? 0;
                    isolation = manifest.Workspace.Isolation.ToString().ToLowerInvariant();
                }
                catch
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

        var rows = new List<IRenderable> { summary };

        if (agents.Count > 0)
        {
            var agentTable = CliTheme.CreateTable("Agents");
            agentTable.AddColumn(CliTheme.StyledColumn("Name"));
            agentTable.AddColumn(CliTheme.StyledColumn("Status"));
            agentTable.AddColumn(CliTheme.StyledColumn("Model"));
            agentTable.AddColumn(CliTheme.StyledColumn("Active").RightAligned());
            agentTable.AddColumn(CliTheme.StyledColumn("Tools"));

            foreach (var agent in agents.OrderBy(a => a.AgentName, StringComparer.Ordinal))
            {
                agentTable.AddRow(
                    $"[bold white]{Markup.Escape(agent.AgentName)}[/]",
                    ColorStatus(agent.Status),
                    Markup.Escape(agent.Model ?? string.Empty),
                    agent.ActiveTasks.Count.ToString(CultureInfo.InvariantCulture),
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

    private static string ColorStatus(string status)
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

    private static string ColorTag(Color tint, string text)
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
        catch
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
        catch
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
        catch
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
        catch
        {
            // stdin redirected — nothing to drain
        }
    }
}
