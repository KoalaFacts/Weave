using System.CommandLine;
namespace Weave.Cli.Commands;

internal static class TuiCommand
{
    public static Command Create(TuiCliCommand handler)
    {
        var cmd = new Command("tui", "Launch the interactive terminal UI");
        cmd.SetAction((_, cancellationToken) => handler.ExecuteAsync(new NoCliOptions(), cancellationToken));
        return cmd;
    }
}
