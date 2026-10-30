using System.CommandLine;
namespace Weave.Cli.Commands;

internal static class VersionCommand
{
    public static Command Create(VersionCliCommand handler)
    {
        var cmd = new Command("version", "Show the installed weave version and cached update info");
        cmd.SetAction((_, cancellationToken) => handler.ExecuteAsync(new NoCliOptions(), cancellationToken));
        return cmd;
    }
}
