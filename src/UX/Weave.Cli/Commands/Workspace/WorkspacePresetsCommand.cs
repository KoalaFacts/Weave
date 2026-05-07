using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspacePresetsCommand
{
    public static Command Create(WorkspacePresetsCliCommand handler)
    {
        var cmd = new Command("presets", "Browse ready-made workspace templates");
        cmd.SetAction((_, cancellationToken) => handler.ExecuteAsync(new NoCliOptions(), cancellationToken));

        return cmd;
    }
}
