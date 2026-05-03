using System.Text.Json;
using Spectre.Console;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

internal sealed class DataImportCliCommand : ICliCommand<DataImportOptions>
{
    public string Name => "import";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Import a workspace from an export file";

    public async Task<int> ExecuteAsync(DataImportOptions options, CancellationToken ct)
    {
        var filePath = SelectFilePath(options.FilePath);

        if (!File.Exists(filePath))
        {
            CliTheme.WriteError($"File not found: {filePath}");
            return 1;
        }

        var export = await ReadExportAsync(filePath, ct);
        if (export is null)
        {
            CliTheme.WriteError("Invalid export file.");
            return 1;
        }

        var name = options.WorkspaceName ?? export.WorkspaceName;
        CliTheme.WriteInfo($"Importing workspace '{name}' (exported {export.ExportedAt:yyyy-MM-dd HH:mm} UTC)...");

        var basePath = await RestoreFilesAsync(export, name, ct);

        using var client = new WorkspaceApiClient();
        if (await client.IsReachableAsync(ct))
            await TryStartImportedWorkspaceAsync(client, export, name, basePath, ct);
        else
            CliTheme.WriteMuted("  Server not running — files restored, start with: weave run " + name);

        Spectre.Console.AnsiConsole.WriteLine();
        CliTheme.WriteSuccess($"Workspace '{name}' imported to {basePath}");

        return 0;
    }

    private static string SelectFilePath(string? filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
            return filePath;

        var exportFiles = FindExportFiles();
        return exportFiles.Count > 0
            ? AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Which export file would you like to import?")
                    .Styled()
                    .AddChoices(exportFiles))
            : AnsiConsole.Prompt(new TextPrompt<string>("Path to export file:").Styled());
    }

    private static List<string> FindExportFiles() =>
        [.. Directory.GetFiles(".", "*-export.json")
            .Concat(Directory.GetFiles(".", "*.export.json"))
            .Concat(Directory.GetFiles(".", "*-backup.json"))
            .Distinct()];

    private static async Task<WorkspaceExport?> ReadExportAsync(string filePath, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(filePath, ct);
        return JsonSerializer.Deserialize(json, DataJsonContext.Default.WorkspaceExport);
    }

    private static async Task<string> RestoreFilesAsync(WorkspaceExport export, string name, CancellationToken ct)
    {
        var basePath = Path.GetFullPath(name);
        Directory.CreateDirectory(basePath);
        Directory.CreateDirectory(Path.Combine(basePath, "prompts"));
        Directory.CreateDirectory(Path.Combine(basePath, "data"));
        Directory.CreateDirectory(Path.Combine(basePath, ".weave"));

        if (!string.IsNullOrWhiteSpace(export.Manifest))
        {
            await File.WriteAllTextAsync(Path.Combine(basePath, "workspace.json"), export.Manifest, ct);
            CliTheme.WriteInfo("  Manifest restored.");
        }

        foreach (var (fileName, content) in export.PromptFiles)
            await File.WriteAllTextAsync(Path.Combine(basePath, "prompts", fileName), content, ct);

        if (export.PromptFiles.Count > 0)
            CliTheme.WriteInfo($"  Prompt files: {export.PromptFiles.Count}");

        WorkspaceRegistry.Register(name, basePath);
        return basePath;
    }

    private static async Task TryStartImportedWorkspaceAsync(
        WorkspaceApiClient client,
        WorkspaceExport export,
        string name,
        string basePath,
        CancellationToken ct)
    {
        try
        {
            var parser = new ManifestParser();
            var manifest = WorkspaceApiClient.PrepareManifest(parser.Parse(export.Manifest ?? "{}"), basePath);
            var response = await client.StartWorkspaceAsync(manifest, ct);
            var statePath = Path.Combine(basePath, ".weave", "workspace-id");
            await File.WriteAllTextAsync(statePath, response.WorkspaceId, ct);
            CliTheme.WriteInfo($"  Workspace started (ID: {response.WorkspaceId}).");

            await RestoreSkillsAsync(client, export, response.WorkspaceId, ct);
            await RestoreChannelsAsync(client, export, response.WorkspaceId, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            CliTheme.WriteWarning($"  Could not start workspace: {ex.Message}");
            CliTheme.WriteMuted("  Files are restored — start manually with: weave run " + name);
        }
    }

    private static async Task RestoreSkillsAsync(WorkspaceApiClient client, WorkspaceExport export, string workspaceId, CancellationToken ct)
    {
        if (export.Skills.Count == 0)
            return;

        var restored = 0;
        var skillErrors = new List<string>();
        foreach (var skill in export.Skills)
        {
            try
            {
                await client.PostSkillAsync(workspaceId, skill, ct);
                restored++;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or IOException)
            {
                skillErrors.Add(ex.Message);
            }
        }

        CliTheme.WriteInfo($"  Skills restored: {restored}/{export.Skills.Count}");
        foreach (var err in skillErrors)
            CliTheme.WriteWarning($"    Skill failed: {err}");
    }

    private static async Task RestoreChannelsAsync(WorkspaceApiClient client, WorkspaceExport export, string workspaceId, CancellationToken ct)
    {
        if (export.Channels.Count == 0)
            return;

        var restored = 0;
        var channelErrors = new List<string>();
        foreach (var channel in export.Channels)
        {
            try
            {
                await client.PostChannelAsync(workspaceId, channel, ct);
                restored++;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or IOException)
            {
                channelErrors.Add(ex.Message);
            }
        }

        CliTheme.WriteInfo($"  Channels restored: {restored}/{export.Channels.Count}");
        foreach (var err in channelErrors)
            CliTheme.WriteWarning($"    Channel failed: {err}");
    }
}
