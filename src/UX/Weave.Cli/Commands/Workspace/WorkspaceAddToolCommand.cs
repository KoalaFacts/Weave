using System.CommandLine;
using Spectre.Console;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Models;

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

            var manifestPath = ManifestResolver.Resolve(workspace);
            if (manifestPath is null)
            {
                CliTheme.WriteError($"No workspace.json found for '{workspace}'.");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(toolName))
            {
                toolName = AnsiConsole.Prompt(new TextPrompt<string>("Tool name:").Styled());
            }

            var parser = new ManifestParser();
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var manifest = parser.Parse(json);

            if (manifest.Tools.ContainsKey(toolName))
            {
                CliTheme.WriteWarning($"Tool '{toolName}' already exists in the workspace.");
                return 1;
            }

            manifest.Tools[toolName] = new ToolDefinition { Type = type };

            await File.WriteAllTextAsync(manifestPath, parser.Serialize(manifest), cancellationToken);

            CliTheme.WriteSuccess($"Tool '{toolName}' added to workspace '{workspace}'.");
            return 0;
        });

        return cmd;
    }
}
