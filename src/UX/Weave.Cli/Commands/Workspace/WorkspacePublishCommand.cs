using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspacePublishCommand
{
    public static Command Create()
    {
        var nameArg = new Argument<string?>("name")
        {
            Description = "Workspace name",
            Arity = ArgumentArity.ZeroOrOne
        };
        nameArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);
        var targetOption = new Option<string?>("--target") { Description = "Deployment target (docker-compose, kubernetes, nomad, fly-io, github-actions)" };
        targetOption.CompletionSources.Add(CliCompletions.CompleteDeployTargets);
        var outputOption = new Option<string>("--output")
        {
            Description = "Output directory",
            DefaultValueFactory = _ => "./output"
        };

        var cmd = new Command("publish", "Generate deployment manifests") { nameArg, targetOption, outputOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArg);
            var target = parseResult.GetValue(targetOption);
            var output = parseResult.GetValue(outputOption)!;
            return await new WorkspacePublishCliCommand().ExecuteAsync(new WorkspacePublishOptions(name, target, output), cancellationToken);
        });

        return cmd;
    }
}
