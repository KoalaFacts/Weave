using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspacePluginListCommand
{
    public static Command Create(WorkspacePluginListCliCommand handler, WorkspaceCompletions completions)
    {
        var workspaceArg = new Argument<string?>("workspace")
        {
            Description = "Workspace name",
            Arity = ArgumentArity.ZeroOrOne
        };
        workspaceArg.CompletionSources.Add(completions.CompleteWorkspaceNames);

        var cmd = new Command("list", "List configured plugins") { workspaceArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = parseResult.GetValue(workspaceArg);
            return await handler.ExecuteAsync(new WorkspaceNameOptions(workspace), cancellationToken);
        });

        return cmd;
    }
}
