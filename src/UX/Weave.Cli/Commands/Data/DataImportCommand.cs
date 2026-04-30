using System.CommandLine;
using System.Text.Json;
using Spectre.Console;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

internal static class DataImportCommand
{
    public static Command Create()
    {
        var fileArg = new Argument<string?>("file")
        {
            Description = "Export file to import",
            Arity = ArgumentArity.ZeroOrOne
        };
        var workspaceOption = new Option<string?>("--workspace") { Description = "Override workspace name" };

        var cmd = new Command("import", "Import a workspace from an export file") { fileArg, workspaceOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var filePath = parseResult.GetValue(fileArg);
            var overrideName = parseResult.GetValue(workspaceOption);

            if (string.IsNullOrWhiteSpace(filePath))
            {
                var exportFiles = Directory.GetFiles(".", "*-export.json")
                    .Concat(Directory.GetFiles(".", "*.export.json"))
                    .Concat(Directory.GetFiles(".", "*-backup.json"))
                    .Distinct()
                    .ToList();

                if (exportFiles.Count > 0)
                {
                    filePath = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Which export file would you like to import?")
                            .Styled()
                            .AddChoices(exportFiles));
                }
                else
                {
                    filePath = AnsiConsole.Prompt(
                        new TextPrompt<string>("Path to export file:")
                            .Styled());
                }
            }

            if (!File.Exists(filePath))
            {
                CliTheme.WriteError($"File not found: {filePath}");
                return 1;
            }

            var json = await File.ReadAllTextAsync(filePath, cancellationToken);
            var export = JsonSerializer.Deserialize(json, DataJsonContext.Default.WorkspaceExport);
            if (export is null)
            {
                CliTheme.WriteError("Invalid export file.");
                return 1;
            }

            var name = overrideName ?? export.WorkspaceName;
            CliTheme.WriteInfo($"Importing workspace '{name}' (exported {export.ExportedAt:yyyy-MM-dd HH:mm} UTC)...");

            var basePath = Path.GetFullPath(name);
            Directory.CreateDirectory(basePath);
            Directory.CreateDirectory(Path.Combine(basePath, "prompts"));
            Directory.CreateDirectory(Path.Combine(basePath, "data"));
            Directory.CreateDirectory(Path.Combine(basePath, ".weave"));

            if (!string.IsNullOrWhiteSpace(export.Manifest))
            {
                await File.WriteAllTextAsync(
                    Path.Combine(basePath, "workspace.json"),
                    export.Manifest,
                    cancellationToken);
                CliTheme.WriteInfo("  Manifest restored.");
            }

            foreach (var (fileName, content) in export.PromptFiles)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(basePath, "prompts", fileName),
                    content,
                    cancellationToken);
            }

            if (export.PromptFiles.Count > 0)
                CliTheme.WriteInfo($"  Prompt files: {export.PromptFiles.Count}");

            WorkspaceRegistry.Register(name, basePath);

            using var client = new WorkspaceApiClient();
            if (await client.IsReachableAsync(cancellationToken))
            {
                try
                {
                    var parser = new ManifestParser();
                    var manifest = WorkspaceApiClient.PrepareManifest(
                        parser.Parse(export.Manifest ?? "{}"), basePath);
                    var response = await client.StartWorkspaceAsync(manifest, cancellationToken);
                    var statePath = Path.Combine(basePath, ".weave", "workspace-id");
                    await File.WriteAllTextAsync(statePath, response.WorkspaceId, cancellationToken);
                    CliTheme.WriteInfo($"  Workspace started (ID: {response.WorkspaceId}).");

                    if (export.Skills.Count > 0)
                    {
                        var restored = 0;
                        var skillErrors = new List<string>();
                        foreach (var skill in export.Skills)
                        {
                            try
                            {
                                await client.PostSkillAsync(response.WorkspaceId, skill, cancellationToken);
                                restored++;
                            }
                            catch (Exception ex)
                            {
                                skillErrors.Add(ex.Message);
                            }
                        }

                        CliTheme.WriteInfo($"  Skills restored: {restored}/{export.Skills.Count}");
                        foreach (var err in skillErrors)
                            CliTheme.WriteWarning($"    Skill failed: {err}");
                    }

                    if (export.Channels.Count > 0)
                    {
                        var restored = 0;
                        var channelErrors = new List<string>();
                        foreach (var channel in export.Channels)
                        {
                            try
                            {
                                await client.PostChannelAsync(response.WorkspaceId, channel, cancellationToken);
                                restored++;
                            }
                            catch (Exception ex)
                            {
                                channelErrors.Add(ex.Message);
                            }
                        }

                        CliTheme.WriteInfo($"  Channels restored: {restored}/{export.Channels.Count}");
                        foreach (var err in channelErrors)
                            CliTheme.WriteWarning($"    Channel failed: {err}");
                    }
                }
                catch (Exception ex)
                {
                    CliTheme.WriteWarning($"  Could not start workspace: {ex.Message}");
                    CliTheme.WriteMuted("  Files are restored — start manually with: weave run " + name);
                }
            }
            else
            {
                CliTheme.WriteMuted("  Server not running — files restored, start with: weave run " + name);
            }

            AnsiConsole.WriteLine();
            CliTheme.WriteSuccess($"Workspace '{name}' imported to {basePath}");

            return 0;
        });

        return cmd;
    }
}
