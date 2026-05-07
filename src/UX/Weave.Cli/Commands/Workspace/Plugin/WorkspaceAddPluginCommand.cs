using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspaceAddPluginCommand
{
    public static Command Create(WorkspaceAddPluginCliCommand handler, WorkspaceCompletions completions)
    {
        var workspaceArg = new Argument<string?>("workspace")
        {
            Description = "Workspace name",
            Arity = ArgumentArity.ZeroOrOne
        };
        workspaceArg.CompletionSources.Add(completions.CompleteWorkspaceNames);
        var nameOption = new Option<string?>("--name") { Description = "Plugin name" };
        var typeOption = new Option<string?>("--type") { Description = "Plugin type (dapr, vault, http, custom)" };
        typeOption.CompletionSources.Add(CliCompletions.CompletePluginTypes);

        var cmd = new Command("plugin", "Add a plugin") { workspaceArg, nameOption, typeOption };
        // Also register as just "add" when used under the plugin branch
        cmd.Aliases.Add("add");
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = parseResult.GetValue(workspaceArg);
            var pluginName = parseResult.GetValue(nameOption);
            var type = parseResult.GetValue(typeOption);
            return await handler.ExecuteAsync(new WorkspaceAddPluginOptions(workspace, pluginName, type), cancellationToken);
        });

        return cmd;
    }
}
