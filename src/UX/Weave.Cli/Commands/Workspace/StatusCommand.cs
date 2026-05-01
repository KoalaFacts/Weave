using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspaceStatusCommand
{
    public static Command Create()
    {
        var nameArg = new Argument<string?>("name")
        {
            Description = "Workspace name",
            Arity = ArgumentArity.ZeroOrOne
        };
        nameArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);

        var cmd = new Command("status", "Show workspace status") { nameArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArg);
            return await new WorkspaceStatusCliCommand().ExecuteAsync(new WorkspaceNameOptions(name), cancellationToken);
        });

        return cmd;
    }
}
