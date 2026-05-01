using Spectre.Console;
using Weave.Cli.Commands;

namespace Weave.Cli.Tui;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from the TUI shell.")]
internal sealed class TuiNewWorkspaceHint
{
    public void Show()
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
}
