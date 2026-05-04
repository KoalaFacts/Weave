using Spectre.Console;
using Weave.Cli.Commands;

namespace Weave.Cli.Tui;

internal static class TuiHelpView
{
    internal static void Render()
    {
        CliTheme.WriteSection("Help");

        RenderGroup("Getting started",
        [
            ("/help", "/?, help", "Show this help"),
            ("/new", "/n", "Hints for creating a workspace"),
            ("/presets", "/p", "Built-in workspace presets"),
            ("/open <ws>", "/o", "Open a workspace for this session"),
        ]);

        RenderGroup("Chat with an agent",
        [
            ("(type anything)", "", "Sends to the current agent"),
            ("/agents", "", "List agents in the current workspace"),
            ("/use <agent>", "/agent, /a", "Switch the current agent"),
            ("/history", "", "Show recent conversation messages"),
        ]);

        RenderGroup("Workspace",
        [
            ("/up", "", "Start the current workspace"),
            ("/down", "", "Stop the current workspace"),
            ("/watch", "", "Live auto-refresh of the current workspace"),
            ("/tools", "", "List tools in the running workspace"),
            ("/tasks", "", "List tasks for the active agent"),
            ("/status", "", "Show workspace status"),
            ("/validate", "", "Validate the workspace manifest"),
        ]);

        RenderGroup("Monitor",
        [
            ("/refresh", "/r", "Re-render the dashboard"),
            ("/system", "/sys", "Silo + config info"),
            ("/webui", "/web, /w", "Open the web dashboard in a browser"),
            ("/ports", "", "Show port assignments"),
            ("/config", "", "View CLI configuration"),
        ]);

        RenderGroup("About this CLI",
        [
            ("/version", "/v", "Installed version + cached update info"),
            ("/upgrade", "/update", "Check NuGet for a newer release"),
        ]);

        RenderGroup("Housekeeping",
        [
            ("/clear", "/cls", "Clear the screen"),
            ("/quit", "/exit, /q, quit", "Exit the TUI"),
        ]);

        AnsiConsole.WriteLine();
        CliTheme.WriteMuted(
            "Tip: anything without a leading slash is sent to the current agent. " +
            "Unknown commands get a \"did you mean?\" suggestion.");
    }

    private static void RenderGroup(string title, (string Cmd, string Aliases, string Desc)[] rows)
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
