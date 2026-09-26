using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspaceUpCommand
{
    public static Command Create(WorkspaceUpCliCommand handler, WorkspaceCompletions completions)
    {
        var nameArg = new Argument<string?>("name")
        {
            Description = "Workspace name",
            Arity = ArgumentArity.ZeroOrOne
        };
        nameArg.CompletionSources.Add(completions.CompleteWorkspaceNames);
        var targetOption = new Option<string>("--target")
        {
            Description = "Deployment target",
            DefaultValueFactory = _ => "local"
        };
        targetOption.CompletionSources.Add(CliCompletions.CompleteDeployTargets);
        var capabilityFileOption = new Option<string?>("--capability-file")
        {
            Description = "File containing an operator-issued workspace installation capability"
        };

        var cmd = new Command("up", "Start a workspace") { nameArg, targetOption, capabilityFileOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArg);
            var target = parseResult.GetValue(targetOption)!;
            var capabilityFile = parseResult.GetValue(capabilityFileOption);
            return await handler.ExecuteAsync(new WorkspaceUpOptions(name, target, capabilityFile), cancellationToken);
        });

        return cmd;
    }
}
