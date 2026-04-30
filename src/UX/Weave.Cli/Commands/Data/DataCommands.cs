using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class DataCommands
{
    public static Command Create()
    {
        var cmd = new Command("data", "Export and import workspace data for migration between storage backends");

        cmd.Subcommands.Add(DataExportCommand.Create());
        cmd.Subcommands.Add(DataImportCommand.Create());

        return cmd;
    }
}
