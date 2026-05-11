using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class DataExportCommand
{
    public static Command Create(DataExportCliCommand handler, WorkspaceCompletions completions)
    {
        var workspaceArg = new Argument<string?>("workspace")
        {
            Description = "Workspace name",
            Arity = ArgumentArity.ZeroOrOne
        };
        workspaceArg.CompletionSources.Add(completions.CompleteWorkspaceNames);
        var outputOption = new Option<string?>("--output", "-o") { Description = "Output file path (defaults to {workspace}-export.json)" };

        var cmd = new Command("export", "Export a complete workspace snapshot to a portable JSON file") { workspaceArg, outputOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = parseResult.GetValue(workspaceArg);
            var output = parseResult.GetValue(outputOption);
            return await handler.ExecuteAsync(new DataExportOptions(workspace, output), cancellationToken);
        });

        return cmd;
    }
}
