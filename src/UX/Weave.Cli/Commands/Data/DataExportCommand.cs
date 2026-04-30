using System.CommandLine;
using System.Text.Json;
using Spectre.Console;

namespace Weave.Cli.Commands;

internal static class DataExportCommand
{
    public static Command Create()
    {
        var workspaceArg = new Argument<string?>("workspace")
        {
            Description = "Workspace name",
            Arity = ArgumentArity.ZeroOrOne
        };
        workspaceArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);
        var outputOption = new Option<string?>("--output", "-o") { Description = "Output file path (defaults to {workspace}-export.json)" };

        var cmd = new Command("export", "Export a complete workspace snapshot to a portable JSON file") { workspaceArg, outputOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = parseResult.GetValue(workspaceArg);
            var output = parseResult.GetValue(outputOption);

            if (string.IsNullOrWhiteSpace(workspace))
            {
                var all = WorkspaceRegistry.GetAll();
                if (all.Count == 0)
                {
                    CliTheme.WriteError("No workspaces found. Create one first with: weave workspace new");
                    return 1;
                }

                workspace = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Which workspace would you like to export?")
                        .Styled()
                        .AddChoices(all.Keys));
            }

            var manifestPath = ManifestResolver.Resolve(workspace);
            if (manifestPath is null)
            {
                CliTheme.WriteError($"No workspace.json found for '{workspace}'.");
                return 1;
            }

            var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
            var outputPath = output ?? $"{workspace}-export.json";

            using var client = new WorkspaceApiClient();
            if (!await client.IsReachableAsync(cancellationToken))
            {
                CliTheme.WriteError("Weave server is not running. Start it with 'weave run'.");
                return 1;
            }

            CliTheme.WriteInfo($"Exporting workspace '{workspace}'...");

            var export = new WorkspaceExport
            {
                ExportedAt = DateTimeOffset.UtcNow,
                WeaveVersion = "1.0",
                WorkspaceName = workspace
            };

            export.Manifest = await File.ReadAllTextAsync(manifestPath, cancellationToken);

            var promptsDir = Path.Combine(manifestDir, "prompts");
            if (Directory.Exists(promptsDir))
            {
                foreach (var file in Directory.GetFiles(promptsDir, "*.md"))
                {
                    var name = Path.GetFileName(file);
                    var content = await File.ReadAllTextAsync(file, cancellationToken);
                    export.PromptFiles[name] = content;
                }
            }

            var statePath = WorkspaceApiClient.GetWorkspaceStatePath(manifestPath);
            var workspaceId = File.Exists(statePath)
                ? (await File.ReadAllTextAsync(statePath, cancellationToken)).Trim()
                : null;

            if (workspaceId is not null)
            {
                export.WorkspaceId = workspaceId;

                try
                {
                    var agents = await client.GetAgentsAsync(workspaceId, cancellationToken);
                    export.Agents = agents;
                    CliTheme.WriteInfo($"  Agents: {agents.Count}");
                }
                catch
                {
                    CliTheme.WriteMuted("  Agents: (not available)");
                }

                try
                {
                    var tools = await client.GetToolsAsync(workspaceId, cancellationToken);
                    export.Tools = tools;
                    CliTheme.WriteInfo($"  Tools: {tools.Count}");
                }
                catch
                {
                    CliTheme.WriteMuted("  Tools: (not available)");
                }

                try
                {
                    var skills = await client.GetSkillsAsync(workspaceId, cancellationToken);
                    export.Skills = skills;
                    CliTheme.WriteInfo($"  Skills: {skills.Count}");
                }
                catch
                {
                    CliTheme.WriteMuted("  Skills: (not available)");
                }

                try
                {
                    var channels = await client.GetChannelsAsync(workspaceId, cancellationToken);
                    export.Channels = channels;
                    CliTheme.WriteInfo($"  Channels: {channels.Count}");
                }
                catch
                {
                    CliTheme.WriteMuted("  Channels: (not available)");
                }
            }

            try
            {
                var marketplace = await client.GetMarketplaceItemsAsync(cancellationToken);
                export.MarketplaceItems = marketplace;
                CliTheme.WriteInfo($"  Marketplace items: {marketplace.Count}");
            }
            catch
            {
                CliTheme.WriteMuted("  Marketplace: (not available)");
            }

            try
            {
                var templates = await client.GetTemplatesAsync(cancellationToken);
                export.Templates = templates;
                CliTheme.WriteInfo($"  Templates: {templates.Count}");
            }
            catch
            {
                CliTheme.WriteMuted("  Templates: (not available)");
            }

            var json = JsonSerializer.Serialize(export, DataJsonContext.Default.WorkspaceExport);
            await File.WriteAllTextAsync(outputPath, json, cancellationToken);

            AnsiConsole.WriteLine();
            CliTheme.WriteSuccess($"Exported to {Path.GetFullPath(outputPath)}");
            CliTheme.WriteMuted($"  File size: {new FileInfo(outputPath).Length / 1024} KB");
            CliTheme.WriteMuted("  Import on another machine with: weave data import " + Path.GetFileName(outputPath));

            return 0;
        });

        return cmd;
    }
}
