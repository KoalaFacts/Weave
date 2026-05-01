using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspaceRemoveCommand
{
    public static Command Create()
    {
        var nameArg = new Argument<string?>("name")
        {
            Description = "Workspace name",
            Arity = ArgumentArity.ZeroOrOne
        };
        nameArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);
        var purgeOption = new Option<bool>("--purge") { Description = "Delete workspace folder" };

        var cmd = new Command("remove", "Remove a workspace") { nameArg, purgeOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArg);
            var purge = parseResult.GetValue(purgeOption);
            return await new WorkspaceRemoveCliCommand().ExecuteAsync(new WorkspaceRemoveOptions(name, purge), cancellationToken);
        });

        return cmd;
    }
}
