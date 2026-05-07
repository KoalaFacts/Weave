using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class DataCommands
{
    public static Command Create(DataExportCliCommand exportHandler, DataImportCliCommand importHandler, WorkspaceCompletions completions)
    {
        var cmd = new Command("data", "Export and import workspace data for migration between storage backends");

        cmd.Subcommands.Add(DataExportCommand.Create(exportHandler, completions));
        cmd.Subcommands.Add(DataImportCommand.Create(importHandler));

        return cmd;
    }
}
