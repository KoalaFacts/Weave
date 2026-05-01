using System.Globalization;
using Spectre.Console;
using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tui;

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

}
