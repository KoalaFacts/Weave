using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspaceListCommand
{
    public static Command Create(WorkspaceListCliCommand handler)
    {
        var cmd = new Command("list", "List all workspaces");
        cmd.SetAction((_, cancellationToken) => handler.ExecuteAsync(new NoCliOptions(), cancellationToken));

        return cmd;
    }
}
