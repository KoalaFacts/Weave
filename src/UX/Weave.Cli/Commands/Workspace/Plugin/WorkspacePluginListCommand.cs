using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspacePluginListCommand
{
    public static Command Create()
    {
        var workspaceArg = new Argument<string>("workspace") { Description = "Workspace name" };
        workspaceArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);

        var cmd = new Command("list", "List configured plugins") { workspaceArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = parseResult.GetValue(workspaceArg)!;
            return await new WorkspacePluginListCliCommand().ExecuteAsync(new WorkspaceNameOptions(workspace), cancellationToken);
        });

        return cmd;
    }
}
