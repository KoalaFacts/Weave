using Spectre.Console;
using Weave.Cli.Commands;

namespace Weave.Cli.Tui;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from the TUI shell.")]
internal sealed class TuiNextStepHint
{
    public void Render(TuiSession session)
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
}
