using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class DataImportCommand
{
    public static Command Create()
    {
        var fileArg = new Argument<string?>("file")
        {
            Description = "Export file to import",
            Arity = ArgumentArity.ZeroOrOne
        };
        var workspaceOption = new Option<string?>("--workspace") { Description = "Override workspace name" };

        var cmd = new Command("import", "Import a workspace from an export file") { fileArg, workspaceOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var filePath = parseResult.GetValue(fileArg);
            var overrideName = parseResult.GetValue(workspaceOption);
            return await new DataImportCliCommand().ExecuteAsync(new DataImportOptions(filePath, overrideName), cancellationToken);
        });

        return cmd;
    }
}
