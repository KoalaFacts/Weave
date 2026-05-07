using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class PortsCommand
{
    public static Command Create(PortsCliCommand handler)
    {
        var cmd = new Command("ports", "Show default port assignments");
        cmd.SetAction((_, cancellationToken) => handler.ExecuteAsync(new NoCliOptions(), cancellationToken));

        return cmd;
    }
}
