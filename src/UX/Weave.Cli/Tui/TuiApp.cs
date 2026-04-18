using System.Globalization;
using Spectre.Console;
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
    private const string ActionNew = "New workspace";
    private const string ActionRefresh = "Refresh";
    private const string ActionQuit = "Quit";

    private const string DetailRefresh = "Refresh";
    private const string DetailStart = "Start workspace (up)";
    private const string DetailStop = "Stop workspace (down)";
    private const string DetailBack = "Back to workspace list";

    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.Clear();
            RenderHeader();

            var workspaces = WorkspaceRegistry.GetAll()
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .ToArray();

            RenderWorkspacesTable(workspaces);

            var choices = new List<string>();
            if (workspaces.Length > 0)
                choices.Add(ActionOpen);
            choices.Add(ActionNew);
            choices.Add(ActionRefresh);
            choices.Add(ActionQuit);

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("What would you like to do?")
                    .Styled()
                    .AddChoices(choices));

            switch (action)
            {
                case ActionOpen:
                    var pick = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Select a workspace:")
                            .Styled()
                            .AddChoices([.. workspaces.Select(w => w.Key), DetailBack]));

                    if (pick != DetailBack)
                        await ShowWorkspaceAsync(pick, cancellationToken);
                    break;

                case ActionNew:
                    ShowNewWorkspaceHint();
                    break;

                case ActionRefresh:
                    continue;

                case ActionQuit:
                    AnsiConsole.Clear();
                    CliTheme.WriteMuted("Goodbye.");
                    return 0;
            }
        }

        return 0;
    }

    private static void RenderHeader()
    {
        CliTheme.WriteBanner();
        CliTheme.WriteMuted("Interactive terminal UI — arrow keys to move, Enter to select.");
        AnsiConsole.WriteLine();
    }

    private static void RenderWorkspacesTable(KeyValuePair<string, string>[] workspaces)
    {
        if (workspaces.Length == 0)
        {
            AnsiConsole.Write(CliTheme.CreatePanel(
                "No workspaces registered yet. Pick \"New workspace\" to get started.",
                "Workspaces"));
            AnsiConsole.WriteLine();
            return;
        }

        var table = CliTheme.CreateTable("Workspaces");
        table.AddColumn(CliTheme.StyledColumn("Name"));
        table.AddColumn(CliTheme.StyledColumn("Path"));
        table.AddColumn(CliTheme.StyledColumn("Manifest"));
        table.AddColumn(CliTheme.StyledColumn("Runtime"));

        foreach (var (name, dir) in workspaces)
        {
            var manifestPath = Path.Combine(dir, "workspace.json");
            var manifestOk = File.Exists(manifestPath);
            var statePath = manifestOk
                ? WorkspaceApiClient.GetWorkspaceStatePath(manifestPath)
                : null;
            var hasState = statePath is not null && File.Exists(statePath);

            var manifestCell = manifestOk
                ? $"[rgb({CliTheme.Success.R},{CliTheme.Success.G},{CliTheme.Success.B})]Ready[/]"
                : $"[rgb({CliTheme.Error.R},{CliTheme.Error.G},{CliTheme.Error.B})]Missing[/]";

            var runtimeCell = hasState
                ? $"[rgb({CliTheme.Info.R},{CliTheme.Info.G},{CliTheme.Info.B})]Known ID[/]"
                : $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]Not started[/]";

            table.AddRow(Markup.Escape(name), Markup.Escape(dir), manifestCell, runtimeCell);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static async Task ShowWorkspaceAsync(string name, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.Clear();
            CliTheme.WriteBanner();
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

            var liveRendered = await TryRenderLiveStatusAsync(manifestPath, manifest, cancellationToken);
            if (!liveRendered)
                RenderManifestView(manifest, manifestPath);

            AnsiConsole.WriteLine();
            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Workspace actions:")
                    .Styled()
                    .AddChoices(DetailRefresh, DetailStart, DetailStop, DetailBack));

            switch (action)
            {
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

    private static async Task<bool> TryRenderLiveStatusAsync(
        string manifestPath,
        WorkspaceManifest manifest,
        CancellationToken cancellationToken)
    {
        var statePath = WorkspaceApiClient.GetWorkspaceStatePath(manifestPath);
        if (!File.Exists(statePath))
            return false;

        string workspaceId;
        try
        {
            workspaceId = (await File.ReadAllTextAsync(statePath, cancellationToken)).Trim();
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(workspaceId))
            return false;

        try
        {
            using var client = new WorkspaceApiClient();
            if (!await client.IsReachableAsync(cancellationToken))
                return false;

            var workspace = await client.GetWorkspaceAsync(workspaceId, cancellationToken);
            var agents = await client.GetAgentsAsync(workspaceId, cancellationToken);
            var tools = await client.GetToolsAsync(workspaceId, cancellationToken);

            var summary = CliTheme.CreateTable("Live Status");
            summary.AddColumn(CliTheme.StyledColumn("Property"));
            summary.AddColumn(CliTheme.StyledColumn("Value"));
            summary.AddRow("Workspace", $"[bold white]{Markup.Escape(manifest.Name)}[/]");
            summary.AddRow("Workspace ID", Markup.Escape(workspace.WorkspaceId));
            summary.AddRow("Status", Markup.Escape(workspace.Status));
            summary.AddRow("Containers", workspace.ContainerCount.ToString(CultureInfo.InvariantCulture));
            summary.AddRow("Manifest", Markup.Escape(manifestPath));
            AnsiConsole.Write(summary);

            if (agents.Count > 0)
            {
                CliTheme.WriteSection("Agents");
                var agentTable = CliTheme.CreateTable();
                agentTable.AddColumn(CliTheme.StyledColumn("Name"));
                agentTable.AddColumn(CliTheme.StyledColumn("Status"));
                agentTable.AddColumn(CliTheme.StyledColumn("Model"));
                agentTable.AddColumn(CliTheme.StyledColumn("Tools"));

                foreach (var agent in agents.OrderBy(a => a.AgentName, StringComparer.Ordinal))
                {
                    agentTable.AddRow(
                        Markup.Escape(agent.AgentName),
                        Markup.Escape(agent.Status),
                        Markup.Escape(agent.Model ?? string.Empty),
                        Markup.Escape(string.Join(", ", agent.ConnectedTools)));
                }

                AnsiConsole.Write(agentTable);
            }

            if (tools.Count > 0)
            {
                CliTheme.WriteSection("Tools");
                var toolTable = CliTheme.CreateTable();
                toolTable.AddColumn(CliTheme.StyledColumn("Name"));
                toolTable.AddColumn(CliTheme.StyledColumn("Type"));
                toolTable.AddColumn(CliTheme.StyledColumn("Status"));

                foreach (var tool in tools.OrderBy(t => t.ToolName, StringComparer.Ordinal))
                    toolTable.AddRow(
                        Markup.Escape(tool.ToolName),
                        Markup.Escape(tool.ToolType),
                        Markup.Escape(tool.Status));

                AnsiConsole.Write(toolTable);
            }

            return true;
        }
        catch (Exception ex)
        {
            CliTheme.WriteWarning($"Live status unavailable: {ex.Message}. Showing manifest instead.");
            return false;
        }
    }

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
            CliTheme.WriteSection("Agents (manifest)");
            var agentTable = CliTheme.CreateTable();
            agentTable.AddColumn(CliTheme.StyledColumn("Name"));
            agentTable.AddColumn(CliTheme.StyledColumn("Model"));
            agentTable.AddColumn(CliTheme.StyledColumn("Tools"));

            foreach (var (agentName, agent) in manifest.Agents)
            {
                agentTable.AddRow(
                    Markup.Escape(agentName),
                    Markup.Escape(agent.Model),
                    Markup.Escape(string.Join(", ", agent.Tools)));
            }

            AnsiConsole.Write(agentTable);
        }

        if (manifest.Tools is { Count: > 0 })
        {
            CliTheme.WriteSection("Tools (manifest)");
            var toolTable = CliTheme.CreateTable();
            toolTable.AddColumn(CliTheme.StyledColumn("Name"));
            toolTable.AddColumn(CliTheme.StyledColumn("Type"));

            foreach (var (toolName, tool) in manifest.Tools)
                toolTable.AddRow(Markup.Escape(toolName), Markup.Escape(tool.Type));

            AnsiConsole.Write(toolTable);
        }
    }

    private static void ShowNewWorkspaceHint()
    {
        AnsiConsole.Clear();
        CliTheme.WriteBanner();
        CliTheme.WriteSection("Create a new workspace");

        var presetNames = WorkspacePresets.All.Keys.ToArray();
        var table = CliTheme.CreateTable("Presets");
        table.AddColumn(CliTheme.StyledColumn("Preset"));
        table.AddColumn(CliTheme.StyledColumn("Description"));

        foreach (var name in presetNames)
        {
            var preset = WorkspacePresets.All[name];
            table.AddRow(Markup.Escape(name), Markup.Escape(preset.Description));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        CliTheme.WriteInfo("Run:   weave workspace new <name>");
        CliTheme.WriteMuted("         weave workspace new <name> --preset <preset>");
        Pause();
    }

    private static void Pause()
    {
        AnsiConsole.WriteLine();
        CliTheme.WriteMuted("Press Enter to continue...");
        _ = Console.ReadLine();
    }
}
