using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspaceDownCommand
{
    public static Command Create(WorkspaceDownCliCommand handler)
    {
        var nameArg = new Argument<string?>("name")
        {
            Description = "Workspace name",
            Arity = ArgumentArity.ZeroOrOne
        };
        nameArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);

        var cmd = new Command("down", "Stop a workspace") { nameArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArg);
            return await handler.ExecuteAsync(new WorkspaceDownOptions(name), cancellationToken);
        });

        return cmd;
    }
}
