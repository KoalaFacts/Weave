using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspaceDownCommand
{
    public static Command Create(WorkspaceDownCliCommand handler, WorkspaceCompletions completions)
    {
        var nameArg = new Argument<string?>("name")
        {
            Description = "Workspace name",
            Arity = ArgumentArity.ZeroOrOne
        };
        nameArg.CompletionSources.Add(completions.CompleteWorkspaceNames);

        var capabilityFileOption = new Option<string?>("--capability-file")
        {
            Description = "File containing an operator-issued workspace stop capability"
        };
        var cmd = new Command("down", "Stop a workspace") { nameArg, capabilityFileOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArg);
            var capabilityFile = parseResult.GetValue(capabilityFileOption);
            return await handler.ExecuteAsync(new WorkspaceDownOptions(name, CapabilityFile: capabilityFile), cancellationToken);
        });

        return cmd;
    }
}
