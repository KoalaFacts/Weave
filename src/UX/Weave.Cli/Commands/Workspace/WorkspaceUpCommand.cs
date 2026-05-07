using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspaceUpCommand
{
    public static Command Create(WorkspaceUpCliCommand handler)
    {
        var nameArg = new Argument<string?>("name")
        {
            Description = "Workspace name",
            Arity = ArgumentArity.ZeroOrOne
        };
        nameArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);
        var targetOption = new Option<string>("--target")
        {
            Description = "Deployment target",
            DefaultValueFactory = _ => "local"
        };
        targetOption.CompletionSources.Add(CliCompletions.CompleteDeployTargets);

        var cmd = new Command("up", "Start a workspace") { nameArg, targetOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArg);
            var target = parseResult.GetValue(targetOption)!;
            return await handler.ExecuteAsync(new WorkspaceUpOptions(name, target), cancellationToken);
        });

        return cmd;
    }
}
