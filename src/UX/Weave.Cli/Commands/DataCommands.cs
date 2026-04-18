using System.CommandLine;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

internal static class DataCommands
{
    public static Command Create()
    {
        var cmd = new Command("data", "Export and import workspace data for migration between storage backends");

        cmd.Subcommands.Add(CreateExportCommand());
        cmd.Subcommands.Add(CreateImportCommand());

        return cmd;
    }

    private static Command CreateExportCommand()
    {
        var workspaceArg = new Argument<string>("workspace") { Description = "Workspace name" };
        workspaceArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);
        var outputOption = new Option<string?>("--output", "-o") { Description = "Output file path (defaults to {workspace}-export.json)" };

        var cmd = new Command("export", "Export a complete workspace snapshot to a portable JSON file") { workspaceArg, outputOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = parseResult.GetValue(workspaceArg)!;
            var output = parseResult.GetValue(outputOption);

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

            // 1. Manifest
            export.Manifest = await File.ReadAllTextAsync(manifestPath, cancellationToken);

            // 2. Prompt files
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

            // 3. Workspace state (to get the workspace ID)
            var statePath = WorkspaceApiClient.GetWorkspaceStatePath(manifestPath);
            var workspaceId = File.Exists(statePath)
                ? (await File.ReadAllTextAsync(statePath, cancellationToken)).Trim()
                : null;

            if (workspaceId is not null)
            {
                export.WorkspaceId = workspaceId;

                // 4. Agents
                try
                {
                    var agents = await client.GetAgentsAsync(workspaceId, cancellationToken);
                    export.Agents = agents;
                    CliTheme.WriteInfo($"  Agents: {agents.Count}");
                }
                catch { CliTheme.WriteMuted("  Agents: (not available)"); }

                // 5. Tools
                try
                {
                    var tools = await client.GetToolsAsync(workspaceId, cancellationToken);
                    export.Tools = tools;
                    CliTheme.WriteInfo($"  Tools: {tools.Count}");
                }
                catch { CliTheme.WriteMuted("  Tools: (not available)"); }

                // 6. Skills
                try
                {
                    var skills = await client.GetSkillsAsync(workspaceId, cancellationToken);
                    export.Skills = skills;
                    CliTheme.WriteInfo($"  Skills: {skills.Count}");
                }
                catch { CliTheme.WriteMuted("  Skills: (not available)"); }

                // 7. Channels
                try
                {
                    var channels = await client.GetChannelsAsync(workspaceId, cancellationToken);
                    export.Channels = channels;
                    CliTheme.WriteInfo($"  Channels: {channels.Count}");
                }
                catch { CliTheme.WriteMuted("  Channels: (not available)"); }
            }

            // 8. Marketplace (global, not per-workspace)
            try
            {
                var marketplace = await client.GetMarketplaceItemsAsync(cancellationToken);
                export.MarketplaceItems = marketplace;
                CliTheme.WriteInfo($"  Marketplace items: {marketplace.Count}");
            }
            catch { CliTheme.WriteMuted("  Marketplace: (not available)"); }

            // 9. Templates (global)
            try
            {
                var templates = await client.GetTemplatesAsync(cancellationToken);
                export.Templates = templates;
                CliTheme.WriteInfo($"  Templates: {templates.Count}");
            }
            catch { CliTheme.WriteMuted("  Templates: (not available)"); }

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

    private static Command CreateImportCommand()
    {
        var fileArg = new Argument<string>("file") { Description = "Export file to import" };
        var workspaceOption = new Option<string?>("--workspace") { Description = "Override workspace name" };

        var cmd = new Command("import", "Import a workspace from an export file") { fileArg, workspaceOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var filePath = parseResult.GetValue(fileArg)!;
            var overrideName = parseResult.GetValue(workspaceOption);

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

            // 1. Create workspace directory and write manifest
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

            // 2. Restore prompt files
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

            // 3. If server is running, restore live state
            using var client = new WorkspaceApiClient();
            if (await client.IsReachableAsync(cancellationToken))
            {
                // Start the workspace
                try
                {
                    var parser = new ManifestParser();
                    var manifest = WorkspaceApiClient.PrepareManifest(
                        parser.Parse(export.Manifest ?? "{}"), basePath);
                    var response = await client.StartWorkspaceAsync(manifest, cancellationToken);
                    var statePath = Path.Combine(basePath, ".weave", "workspace-id");
                    await File.WriteAllTextAsync(statePath, response.WorkspaceId, cancellationToken);
                    CliTheme.WriteInfo($"  Workspace started (ID: {response.WorkspaceId}).");

                    // Restore skills
                    if (export.Skills.Count > 0)
                    {
                        var restored = 0;
                        foreach (var skill in export.Skills)
                        {
                            try
                            {
                                await client.PostSkillAsync(response.WorkspaceId, skill, cancellationToken);
                                restored++;
                            }
                            catch { }
                        }

                        CliTheme.WriteInfo($"  Skills restored: {restored}/{export.Skills.Count}");
                    }

                    // Restore channels
                    if (export.Channels.Count > 0)
                    {
                        var restored = 0;
                        foreach (var channel in export.Channels)
                        {
                            try
                            {
                                await client.PostChannelAsync(response.WorkspaceId, channel, cancellationToken);
                                restored++;
                            }
                            catch { }
                        }

                        CliTheme.WriteInfo($"  Channels restored: {restored}/{export.Channels.Count}");
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

internal sealed record WorkspaceExport
{
    public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.UtcNow;
    public string WeaveVersion { get; init; } = "1.0";
    public string WorkspaceName { get; init; } = string.Empty;
    public string? WorkspaceId { get; set; }
    public string? Manifest { get; set; }
    public Dictionary<string, string> PromptFiles { get; init; } = [];
    public IReadOnlyList<ApiAgentResponse> Agents { get; set; } = [];
    public IReadOnlyList<ApiToolResponse> Tools { get; set; } = [];
    public IReadOnlyList<JsonElement> Skills { get; set; } = [];
    public IReadOnlyList<JsonElement> Channels { get; set; } = [];
    public IReadOnlyList<ApiMarketplaceItemResponse> MarketplaceItems { get; set; } = [];
    public IReadOnlyList<JsonElement> Templates { get; set; } = [];
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WorkspaceExport))]
[JsonSerializable(typeof(List<JsonElement>))]
internal sealed partial class DataJsonContext : JsonSerializerContext;
