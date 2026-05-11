using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspaceShowCommand
{
    public static Command Create(WorkspaceShowCliCommand handler, WorkspaceCompletions completions)
    {
        var nameArg = new Argument<string?>("name")
        {
            Description = "Workspace name",
            Arity = ArgumentArity.ZeroOrOne
        };
        nameArg.CompletionSources.Add(completions.CompleteWorkspaceNames);

        var cmd = new Command("show", "Show workspace configuration") { nameArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArg);
            return await handler.ExecuteAsync(new WorkspaceNameOptions(name), cancellationToken);
        });

        return cmd;
    }
}
