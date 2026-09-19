using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspaceNewCommand
{
    public static Command Create(WorkspaceNewCliCommand handler)
    {
        var nameArg = new Argument<string?>("name")
        {
            Description = "Workspace name",
            Arity = ArgumentArity.ZeroOrOne
        };
        var presetOption = new Option<string?>("--preset") { Description = "Use a built-in preset (starter, coding-assistant, research, multi-agent, support-team)" };
        presetOption.CompletionSources.Add(CliCompletions.CompletePresetNames);
        var pathOption = new Option<string?>("--path") { Description = "Folder path for the workspace (defaults to ./{name})" };

        var cmd = new Command("new", "Create a new workspace") { nameArg, presetOption, pathOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArg);
            var preset = parseResult.GetValue(presetOption);
            var explicitPath = parseResult.GetValue(pathOption);
            return await handler.ExecuteAsync(new WorkspaceNewOptions(name, preset, explicitPath), cancellationToken);
        });

        return cmd;
    }
}
