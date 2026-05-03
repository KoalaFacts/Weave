using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspaceAddTargetCommand
{
    public static Command Create()
    {
        var workspaceArg = new Argument<string?>("workspace")
        {
            Description = "Workspace name",
            Arity = ArgumentArity.ZeroOrOne
        };
        workspaceArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);
        var nameOption = new Option<string?>("--name") { Description = "Target name" };
        var runtimeOption = new Option<string>("--runtime")
        {
            Description = "Runtime type (podman, docker)",
            DefaultValueFactory = _ => "podman"
        };
        runtimeOption.CompletionSources.Add(CliCompletions.CompleteRuntimeTypes);

        var cmd = new Command("target", "Add a deployment target") { workspaceArg, nameOption, runtimeOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = parseResult.GetValue(workspaceArg);
            var targetName = parseResult.GetValue(nameOption);
            var runtime = parseResult.GetValue(runtimeOption)!;
            return await new WorkspaceAddTargetCliCommand().ExecuteAsync(new WorkspaceAddTargetOptions(workspace, targetName, runtime), cancellationToken);
        });

        return cmd;
    }
}
