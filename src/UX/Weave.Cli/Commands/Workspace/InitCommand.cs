using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class InitCommand
{
    public static Command Create()
    {
        var cmd = new Command("init", "Set up the Weave environment on this machine");
        cmd.SetAction(async (_, cancellationToken) =>
            await new InitCliCommand().ExecuteAsync(new NoCliOptions(), cancellationToken));
        return cmd;
    }
}
