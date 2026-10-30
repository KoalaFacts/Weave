using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspaceValidateCommand
{
    public static Command Create(WorkspaceValidateCliCommand handler, WorkspaceCompletions completions)
    {
        var nameArg = new Argument<string?>("name")
        {
            Description = "Workspace name",
            Arity = ArgumentArity.ZeroOrOne
        };
        nameArg.CompletionSources.Add(completions.CompleteWorkspaceNames);

        var cmd = new Command("validate", "Validate workspace configuration") { nameArg };
        cmd.SetAction((parseResult, ct) =>
            handler.ExecuteAsync(new WorkspaceNameOptions(parseResult.GetValue(nameArg)), ct));

        return cmd;
    }
}
