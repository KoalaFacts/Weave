using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class DataImportCommand
{
    public static Command Create(DataImportCliCommand handler)
    {
        var fileArg = new Argument<string?>("file")
        {
            Description = "Export file to import",
            Arity = ArgumentArity.ZeroOrOne
        };
        var workspaceOption = new Option<string?>("--workspace") { Description = "Override workspace name" };
        var capabilityFileOption = new Option<string?>("--capability-file")
        {
            Description = "File containing an operator-issued workspace installation capability"
        };

        var cmd = new Command("import", "Import a workspace from an export file")
        { fileArg, workspaceOption, capabilityFileOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var filePath = parseResult.GetValue(fileArg);
            var overrideName = parseResult.GetValue(workspaceOption);
            var capabilityFile = parseResult.GetValue(capabilityFileOption);
            return await handler.ExecuteAsync(new DataImportOptions(filePath, overrideName, capabilityFile), cancellationToken);
        });

        return cmd;
    }
}
