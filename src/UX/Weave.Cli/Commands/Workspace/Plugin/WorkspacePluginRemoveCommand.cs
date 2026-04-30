using System.CommandLine;
using Spectre.Console;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

internal static class WorkspacePluginRemoveCommand
{
    public static Command Create()
    {
        var workspaceArg = new Argument<string>("workspace") { Description = "Workspace name" };
        workspaceArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);
        var nameOption = new Option<string?>("--name") { Description = "Plugin name to remove" };

        var cmd = new Command("remove", "Remove a plugin") { workspaceArg, nameOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = parseResult.GetValue(workspaceArg)!;
            var pluginName = parseResult.GetValue(nameOption);

            var manifestPath = ManifestResolver.Resolve(workspace);
            if (manifestPath is null)
            {
                CliTheme.WriteError($"No workspace.json found for '{workspace}'.");
                return 1;
            }

            var parser = new ManifestParser();
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var manifest = parser.Parse(json);

            if (manifest.Plugins.Count == 0)
            {
                CliTheme.WriteWarning("No plugins configured in this workspace.");
                return 0;
            }

            if (string.IsNullOrWhiteSpace(pluginName))
            {
                pluginName = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Select plugin to remove:")
                        .Styled()
                        .AddChoices(manifest.Plugins.Keys));
            }

            if (!manifest.Plugins.Remove(pluginName))
            {
                CliTheme.WriteWarning($"Plugin '{pluginName}' not found in the workspace.");
                return 1;
            }

            await File.WriteAllTextAsync(manifestPath, parser.Serialize(manifest), cancellationToken);

            CliTheme.WriteSuccess($"Plugin '{pluginName}' removed from workspace '{workspace}'.");
            return 0;
        });

        return cmd;
    }
}
