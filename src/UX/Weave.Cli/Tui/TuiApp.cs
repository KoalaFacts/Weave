using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;
using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Models;

namespace Weave.Cli.Tui;

/// <summary>
/// Full-screen interactive terminal UI over the existing CLI primitives.
/// Menu-driven so it works on any terminal that Spectre.Console supports.
/// </summary>
internal static class TuiApp
{
    private const string ActionOpen = "Open workspace";
    private const string ActionNew = "Create new workspace";
    private const string ActionPresets = "Browse presets";
    private const string ActionSystem = "System info";
    private const string ActionRefresh = "Refresh";
    private const string ActionQuit = "Quit";

    private const string DetailWatch = "Watch live status (auto-refresh)";
    private const string DetailRefresh = "Refresh once";
    private const string DetailStart = "Start workspace (up)";
    private const string DetailStop = "Stop workspace (down)";
    private const string DetailBack = "← Back to workspace list";

    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var firstPaint = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.Clear();
            if (firstPaint)
            {
                CliTheme.WriteBanner();
                firstPaint = false;
            }
            else
            {
                RenderCompactHeader();
            }

            var workspaces = WorkspaceRegistry.GetAll()
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .ToArray();

            var siloReachable = await ProbeSiloAsync(cancellationToken);
            RenderDashboardStats(workspaces, siloReachable);
            RenderWorkspacesTable(workspaces);
            RenderKeyHintFooter();

            var choices = new List<string>();
            if (workspaces.Length > 0)
                choices.Add(ActionOpen);
            choices.Add(ActionNew);
            choices.Add(ActionPresets);
            choices.Add(ActionSystem);
            choices.Add(ActionRefresh);
            choices.Add(ActionQuit);

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"[rgb({CliTheme.Accent.R},{CliTheme.Accent.G},{CliTheme.Accent.B})]◆[/] Choose an action")
                    .Styled()
                    .PageSize(10)
                    .AddChoices(choices));

            switch (action)
            {
                case ActionOpen:
                    var pick = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Select a workspace:")
                            .Styled()
                            .PageSize(15)
                            .AddChoices([.. workspaces.Select(w => w.Key), DetailBack]));

                    if (pick != DetailBack)
                        await ShowWorkspaceAsync(pick, cancellationToken);
                    break;

                case ActionNew:
                    ShowNewWorkspaceHint();
                    break;

                case ActionPresets:
                    ShowPresetsScreen();
                    break;

                case ActionSystem:
                    await ShowSystemScreenAsync(cancellationToken);
                    break;

                case ActionRefresh:
                    continue;

                case ActionQuit:
                    AnsiConsole.Clear();
                    AnsiConsole.MarkupLine(
                        $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]See you next weave.[/]");
                    return 0;
            }
        }

        return 0;
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
                "No workspaces registered yet. Pick \"Create new workspace\" to get started.",
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
                    // Treat as invalid manifest — counts stay zero.
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

    private static void RenderKeyHintFooter()
    {
        AnsiConsole.MarkupLine(
            $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]" +
            $"↑/↓ navigate  ·  ⏎ select  ·  choose [bold]Quit[/] or press Ctrl+C to exit[/]");
        AnsiConsole.WriteLine();
    }

    private static async Task ShowWorkspaceAsync(string name, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.Clear();
            RenderCompactHeader();
            CliTheme.WriteSection($"Workspace · {name}");

            var manifestPath = ManifestResolver.Resolve(name);
            if (manifestPath is null)
            {
                CliTheme.WriteError($"No workspace.json found for '{name}'.");
                Pause();
                return;
            }

            WorkspaceManifest manifest;
            try
            {
                var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                manifest = new ManifestParser().Parse(json);
            }
            catch (Exception ex)
            {
                CliTheme.WriteError($"Failed to parse manifest: {ex.Message}");
                Pause();
                return;
            }

            var liveRendered = await TryRenderLiveStatusOnceAsync(manifestPath, manifest, cancellationToken);
            if (!liveRendered)
                RenderManifestView(manifest, manifestPath);

            AnsiConsole.WriteLine();
            var hasState = File.Exists(WorkspaceApiClient.GetWorkspaceStatePath(manifestPath));
            var choices = new List<string>();
            if (hasState)
                choices.Add(DetailWatch);
            choices.Add(DetailRefresh);
            choices.Add(DetailStart);
            choices.Add(DetailStop);
            choices.Add(DetailBack);

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Workspace actions:")
                    .Styled()
                    .PageSize(10)
                    .AddChoices(choices));

            switch (action)
            {
                case DetailWatch:
                    await WatchLiveStatusAsync(manifestPath, manifest, cancellationToken);
                    break;

                case DetailRefresh:
                    continue;

                case DetailStart:
                    CliTheme.WriteMuted($"  Run: weave workspace up {name}");
                    Pause();
                    break;

                case DetailStop:
                    CliTheme.WriteMuted($"  Run: weave workspace down {name}");
                    Pause();
                    break;

                case DetailBack:
                    return;
            }
        }
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
            Pause();
            return;
        }

        AnsiConsole.Clear();
        RenderCompactHeader();
        CliTheme.WriteSection($"Watching · {manifest.Name}");
        AnsiConsole.MarkupLine(
            $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]Auto-refresh every 2s · press any key to return[/]");
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
            _ when lower.Contains("error", StringComparison.Ordinal) || lower.Contains("fail", StringComparison.Ordinal) => CliTheme.Error,
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
        AnsiConsole.Clear();
        RenderCompactHeader();
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
        Pause();
    }

    private static void ShowPresetsScreen()
    {
        AnsiConsole.Clear();
        RenderCompactHeader();
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
        AnsiConsole.WriteLine();
        CliTheme.WriteMuted("Use: weave workspace new <name> --preset <preset>");
        Pause();
    }

    private static async Task ShowSystemScreenAsync(CancellationToken cancellationToken)
    {
        AnsiConsole.Clear();
        RenderCompactHeader();
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
        Pause();
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

    private static void Pause()
    {
        AnsiConsole.WriteLine();
        CliTheme.WriteMuted("Press Enter to continue...");
        _ = Console.ReadLine();
    }
}
