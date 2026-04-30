using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspaceListCommand
{
    public static Command Create()
    {
        var cmd = new Command("list", "List all workspaces");
        cmd.SetAction((_, cancellationToken) => new WorkspaceListCliCommand().ExecuteAsync(new NoCliOptions(), cancellationToken));

        return cmd;
    }
}
