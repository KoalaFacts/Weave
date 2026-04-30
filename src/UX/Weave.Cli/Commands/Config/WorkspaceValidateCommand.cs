using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspaceValidateCommand
{
    public static Command Create()
    {
        var nameArg = new Argument<string>("name") { Description = "Workspace name" };
        nameArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);

        var cmd = new Command("validate", "Validate workspace configuration") { nameArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            return await new WorkspaceValidateCliCommand().ExecuteAsync(new WorkspaceNameOptions(name), cancellationToken);
        });

        return cmd;
    }
}
