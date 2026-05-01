using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspaceShowCommand
{
    public static Command Create()
    {
        var nameArg = new Argument<string?>("name")
        {
            Description = "Workspace name",
            Arity = ArgumentArity.ZeroOrOne
        };
        nameArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);

        var cmd = new Command("show", "Show workspace configuration") { nameArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArg);
            return await new WorkspaceShowCliCommand().ExecuteAsync(new WorkspaceNameOptions(name), cancellationToken);
        });

        return cmd;
    }
}
