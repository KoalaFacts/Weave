using System.CommandLine;
using Weave.Cli.Tui;

namespace Weave.Cli.Commands;

internal static class TuiCommand
{
    public static Command Create()
    {
        var cmd = new Command("tui", "Launch the interactive terminal UI");
        cmd.SetAction((_, cancellationToken) => TuiApp.RunAsync(cancellationToken));
        return cmd;
    }
}
