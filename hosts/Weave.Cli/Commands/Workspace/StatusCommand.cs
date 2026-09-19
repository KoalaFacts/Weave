using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspaceStatusCommand
{
    public static Command Create(WorkspaceStatusCliCommand handler, WorkspaceCompletions completions)
    {
        var nameArg = new Argument<string?>("name")
        {
            Description = "Workspace name",
            Arity = ArgumentArity.ZeroOrOne
        };
        nameArg.CompletionSources.Add(completions.CompleteWorkspaceNames);

        var cmd = new Command("status", "Show workspace status") { nameArg };
        cmd.SetAction((parseResult, ct) =>
            handler.ExecuteAsync(new WorkspaceNameOptions(parseResult.GetValue(nameArg)), ct));

        return cmd;
    }
}
