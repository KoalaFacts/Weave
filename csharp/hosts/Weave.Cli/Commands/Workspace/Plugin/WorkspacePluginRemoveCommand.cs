using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspacePluginRemoveCommand
{
    public static Command Create(WorkspacePluginRemoveCliCommand handler, WorkspaceCompletions completions)
    {
        var workspaceArg = new Argument<string?>("workspace")
        {
            Description = "Workspace name",
            Arity = ArgumentArity.ZeroOrOne
        };
        workspaceArg.CompletionSources.Add(completions.CompleteWorkspaceNames);
        var nameOption = new Option<string?>("--name") { Description = "Plugin name to remove" };

        var cmd = new Command("remove", "Remove a plugin") { workspaceArg, nameOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = parseResult.GetValue(workspaceArg);
            var pluginName = parseResult.GetValue(nameOption);
            return await handler.ExecuteAsync(new WorkspacePluginRemoveOptions(workspace, pluginName), cancellationToken);
        });

        return cmd;
    }
}
