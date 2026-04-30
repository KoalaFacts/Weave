using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspaceAddToolCommand
{
    public static Command Create()
    {
        var workspaceArg = new Argument<string>("workspace") { Description = "Workspace name" };
        workspaceArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);
        var nameOption = new Option<string?>("--name") { Description = "Tool name" };
        var typeOption = new Option<string>("--type")
        {
            Description = "Tool type (mcp, cli, openapi, direct_http, dapr, filesystem)",
            DefaultValueFactory = _ => "mcp"
        };
        typeOption.CompletionSources.Add(CliCompletions.CompleteToolTypes);

        var cmd = new Command("tool", "Add a tool") { workspaceArg, nameOption, typeOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = parseResult.GetValue(workspaceArg)!;
            var toolName = parseResult.GetValue(nameOption);
            var type = parseResult.GetValue(typeOption)!;
            return await new WorkspaceAddToolCliCommand().ExecuteAsync(new WorkspaceAddToolOptions(workspace, toolName, type), cancellationToken);
        });

        return cmd;
    }
}
