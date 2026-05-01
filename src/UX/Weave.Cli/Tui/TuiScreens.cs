using System.Globalization;
using Spectre.Console;
using Weave.Cli.Commands;

namespace Weave.Cli.Tui;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from the TUI shell.")]
internal sealed class TuiScreens
{
    public void RenderWelcomeHint()
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

    public void RenderSlashHelp()
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

    public void ShowConfig()
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

    public void RenderNextStepHint(TuiSession session)
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

    public void ShowNewWorkspaceHint()
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

    public async Task ShowSystemAsync(CancellationToken cancellationToken)
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
}
