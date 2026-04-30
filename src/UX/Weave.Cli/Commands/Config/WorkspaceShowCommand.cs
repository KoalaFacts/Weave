using System.CommandLine;
using Spectre.Console;

namespace Weave.Cli.Commands;

internal static class WorkspaceShowCommand
{
    public static Command Create()
    {
        var nameArg = new Argument<string>("name") { Description = "Workspace name" };
        nameArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);

        var cmd = new Command("show", "Show workspace configuration") { nameArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArg)!;

            var manifestPath = ManifestResolver.Resolve(name);
            if (manifestPath is null)
            {
                CliTheme.WriteError($"No workspace.json found for '{name}'.");
                return 1;
            }

            var content = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            AnsiConsole.Write(CliTheme.CreatePanel(content, "workspace.json"));
            return 0;
        });

        return cmd;
    }
}
