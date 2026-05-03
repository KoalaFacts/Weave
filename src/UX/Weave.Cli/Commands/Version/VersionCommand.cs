using System.CommandLine;
namespace Weave.Cli.Commands;

internal static class VersionCommand
{
    public static Command Create()
    {
        var cmd = new Command("version", "Show the installed weave version and cached update info");
        cmd.SetAction((_, cancellationToken) => new VersionCliCommand().ExecuteAsync(new NoCliOptions(), cancellationToken));
        return cmd;
    }
}
