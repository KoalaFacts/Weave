using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspaceAddAgentCommand
{
    public static Command Create()
    {
        var workspaceArg = new Argument<string>("workspace") { Description = "Workspace name" };
        workspaceArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);
        var nameOption = new Option<string?>("--name") { Description = "Agent name" };
        var modelOption = new Option<string>("--model")
        {
            Description = "Model to use",
            DefaultValueFactory = _ => "claude-sonnet-4-20250514"
        };

        var cmd = new Command("agent", "Add an assistant") { workspaceArg, nameOption, modelOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = parseResult.GetValue(workspaceArg)!;
            var agentName = parseResult.GetValue(nameOption);
            var model = parseResult.GetValue(modelOption)!;
            return await new WorkspaceAddAgentCliCommand().ExecuteAsync(new WorkspaceAddAgentOptions(workspace, agentName, model), cancellationToken);
        });

        return cmd;
    }
}
