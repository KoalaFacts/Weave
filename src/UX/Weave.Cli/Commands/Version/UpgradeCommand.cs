using System.CommandLine;
namespace Weave.Cli.Commands;

internal static class UpgradeCommand
{
    public static Command Create()
    {
        var cmd = new Command("upgrade", "Check NuGet for a newer weave release");
        cmd.SetAction((_, cancellationToken) => new UpgradeCliCommand().ExecuteAsync(new NoCliOptions(), cancellationToken));
        return cmd;
    }
}
