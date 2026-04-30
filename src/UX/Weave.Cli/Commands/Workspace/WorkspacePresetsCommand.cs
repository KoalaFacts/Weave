using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspacePresetsCommand
{
    public static Command Create()
    {
        var cmd = new Command("presets", "Browse ready-made workspace templates");
        cmd.SetAction((_, cancellationToken) => new WorkspacePresetsCliCommand().ExecuteAsync(new NoCliOptions(), cancellationToken));

        return cmd;
    }
}
