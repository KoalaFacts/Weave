using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;
using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Models;

namespace Weave.Cli.Tui;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from the TUI shell.")]
internal sealed class TuiWorkspaceDashboard
{
    private readonly ManifestParser _parser = new();

    public async Task RefreshAsync(CancellationToken ct)
    {
        var workspaces = WorkspaceRegistry.GetAll()
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .ToArray();
        var siloReachable = await TuiRuntimeProbe.ProbeSiloAsync(ct);
        RenderDashboardStats(workspaces, siloReachable);
        RenderWorkspacesTable(workspaces);
    }

    public async Task<bool> TryRenderLiveStatusOnceAsync(
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

    public async Task WatchLiveStatusAsync(
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

                        for (var index = 0; index < 20 && !lifetime.IsCancellationRequested; index++)
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
        }
    }

    public void RenderManifestView(WorkspaceManifest manifest, string manifestPath)
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
            RenderManifestAgents(manifest);

        if (manifest.Tools is { Count: > 0 })
            RenderManifestTools(manifest);
    }

    private void RenderWorkspacesTable(KeyValuePair<string, string>[] workspaces)
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
            AddWorkspaceRow(table, name, dir);

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private void AddWorkspaceRow(Table table, string name, string dir)
    {
        var manifestPath = Path.Combine(dir, "workspace.json");
        var manifestOk = File.Exists(manifestPath);

        int agents = 0, tools = 0;
        var isolation = "—";

        if (manifestOk)
        {
            try
            {
                var manifest = _parser.Parse(File.ReadAllText(manifestPath));
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

        table.AddRow(
            $"[bold white]{Markup.Escape(name)}[/]",
            agents.ToString(CultureInfo.InvariantCulture),
            tools.ToString(CultureInfo.InvariantCulture),
            Markup.Escape(isolation),
            manifestOk
                ? TuiMarkup.ColorTag(CliTheme.Success, "● Ready")
                : TuiMarkup.ColorTag(CliTheme.Error, "✖ Missing"),
            hasState
                ? TuiMarkup.ColorTag(CliTheme.Info, "● Tracked")
                : TuiMarkup.ColorTag(CliTheme.Muted, "○ Idle"));
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
        summary.AddRow("Status", TuiMarkup.ColorStatus(workspace.Status));
        summary.AddRow("Containers", workspace.ContainerCount.ToString(CultureInfo.InvariantCulture));
        if (workspace.StartedAt is { } started)
            summary.AddRow("Started", started.ToLocalTime().ToString("u", CultureInfo.InvariantCulture));
        summary.AddRow("Manifest", Markup.Escape(manifestPath));
        summary.AddRow("Refreshed", DateTime.Now.ToString("T", CultureInfo.InvariantCulture));

        var rows = new List<IRenderable> { summary };
        AddAgentStatusTable(rows, agents);
        AddToolStatusTable(rows, tools);
        return new Rows(rows);
    }

    private static void AddAgentStatusTable(List<IRenderable> rows, IReadOnlyList<ApiAgentResponse> agents)
    {
        if (agents.Count == 0)
            return;

        var agentTable = CliTheme.CreateTable("Agents");
        agentTable.AddColumn(CliTheme.StyledColumn("Name"));
        agentTable.AddColumn(CliTheme.StyledColumn("Status"));
        agentTable.AddColumn(CliTheme.StyledColumn("Model"));
        agentTable.AddColumn(CliTheme.StyledColumn("Active Tasks"));
        agentTable.AddColumn(CliTheme.StyledColumn("Tools"));

        foreach (var agent in agents.OrderBy(agent => agent.AgentName, StringComparer.Ordinal))
        {
            var taskSummary = agent.ActiveTasks.Count == 0
                ? "—"
                : string.Join(", ", agent.ActiveTasks.Select(task => task.Description));

            agentTable.AddRow(
                $"[bold white]{Markup.Escape(agent.AgentName)}[/]",
                TuiMarkup.ColorStatus(agent.Status),
                Markup.Escape(agent.Model ?? string.Empty),
                Markup.Escape(taskSummary),
                Markup.Escape(string.Join(", ", agent.ConnectedTools)));
        }

        rows.Add(agentTable);
    }

    private static void AddToolStatusTable(List<IRenderable> rows, IReadOnlyList<ApiToolResponse> tools)
    {
        if (tools.Count == 0)
            return;

        var toolTable = CliTheme.CreateTable("Tools");
        toolTable.AddColumn(CliTheme.StyledColumn("Name"));
        toolTable.AddColumn(CliTheme.StyledColumn("Type"));
        toolTable.AddColumn(CliTheme.StyledColumn("Status"));

        foreach (var tool in tools.OrderBy(tool => tool.ToolName, StringComparer.Ordinal))
            toolTable.AddRow(
                $"[bold white]{Markup.Escape(tool.ToolName)}[/]",
                Markup.Escape(tool.ToolType),
                TuiMarkup.ColorStatus(tool.Status));

        rows.Add(toolTable);
    }

    private static void RenderManifestAgents(WorkspaceManifest manifest)
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

    private static void RenderManifestTools(WorkspaceManifest manifest)
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

    private static void RenderCompactHeader()
    {
        var rule = new Rule(
            $"[bold rgb({CliTheme.Primary.R},{CliTheme.Primary.G},{CliTheme.Primary.B})]◆ Weave[/] " +
            $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]· TUI[/]")
            .RuleStyle(CliTheme.MutedStyle)
            .LeftJustified();
        AnsiConsole.Write(rule);
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
        }
    }
}
