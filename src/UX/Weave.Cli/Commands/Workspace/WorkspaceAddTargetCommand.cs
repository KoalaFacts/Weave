using System.CommandLine;
using Spectre.Console;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal static class WorkspaceAddTargetCommand
{
    public static Command Create()
    {
        var workspaceArg = new Argument<string>("workspace") { Description = "Workspace name" };
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
            var workspace = parseResult.GetValue(workspaceArg)!;
            var targetName = parseResult.GetValue(nameOption);
            var runtime = parseResult.GetValue(runtimeOption)!;

            var manifestPath = ManifestResolver.Resolve(workspace);
            if (manifestPath is null)
            {
                CliTheme.WriteError($"No workspace.json found for '{workspace}'.");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(targetName))
            {
                targetName = AnsiConsole.Prompt(new TextPrompt<string>("Target name:").Styled());
            }

            var parser = new ManifestParser();
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var manifest = parser.Parse(json);

            if (manifest.Targets.ContainsKey(targetName))
            {
                CliTheme.WriteWarning($"Target '{targetName}' already exists in the workspace.");
                return 1;
            }

            manifest.Targets[targetName] = new TargetDefinition { Runtime = runtime };

            await File.WriteAllTextAsync(manifestPath, parser.Serialize(manifest), cancellationToken);

            CliTheme.WriteSuccess($"Target '{targetName}' added to workspace '{workspace}'.");
            return 0;
        });

        return cmd;
    }
}
