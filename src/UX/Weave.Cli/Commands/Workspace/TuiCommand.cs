using System.CommandLine;
namespace Weave.Cli.Commands;

internal static class TuiCommand
{
    public static Command Create()
    {
        var cmd = new Command("tui", "Launch the interactive terminal UI");
        cmd.SetAction((_, cancellationToken) => new TuiCliCommand().ExecuteAsync(new NoCliOptions(), cancellationToken));
        return cmd;
    }
}
