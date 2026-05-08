using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class InitCommand
{
    public static Command Create(InitCliCommand handler)
    {
        var cmd = new Command("init", "Set up the Weave environment on this machine");
        cmd.SetAction(async (_, cancellationToken) =>
            await handler.ExecuteAsync(new NoCliOptions(), cancellationToken));
        return cmd;
    }
}
