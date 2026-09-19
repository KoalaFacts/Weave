using System.Text.Json;
using Spectre.Console;
using Weave.Actions.Channel;
using Weave.Actions.Context;
using Weave.Actions.Skill;
using Weave.Actions.SystemInfo;
using Weave.Actions.Workspace;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

internal sealed class DataImportCliCommand(
    IWorkspaceRegistry registry,
    GetSystemInfoAction systemInfoAction,
    StartWorkspaceAction startAction,
    PostSkillAction postSkillAction,
    PostChannelAction postChannelAction) : ICliCommand<DataImportOptions>
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

        var systemInfo = await systemInfoAction.ExecuteAsync(new GetSystemInfoInput(), ct);
        if (systemInfo.IsSuccess && systemInfo.Value.Reachable)
            await TryStartImportedWorkspaceAsync(export, name, basePath, ct);
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

    private async Task<string> RestoreFilesAsync(WorkspaceExport export, string name, CancellationToken ct)
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

        registry.Register(name, basePath);
        return basePath;
    }

    private async Task TryStartImportedWorkspaceAsync(
        WorkspaceExport export,
        string name,
        string basePath,
        CancellationToken ct)
    {
        WorkspaceManifest manifest;
        try
        {
            var parser = new ManifestParser();
            manifest = WorkspaceManifestPaths.PrepareForSilo(parser.Parse(export.Manifest ?? "{}"), basePath);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            CliTheme.WriteWarning($"  Could not parse manifest: {ex.Message}");
            CliTheme.WriteMuted("  Files are restored — start manually with: weave run " + name);
            return;
        }

        var startResult = await startAction.ExecuteAsync(new StartWorkspaceInput(manifest), ct);
        if (!startResult.IsSuccess)
        {
            CliTheme.WriteWarning($"  Could not start workspace: {startResult.Failure.Message}");
            CliTheme.WriteMuted("  Files are restored — start manually with: weave run " + name);
            return;
        }

        var workspaceId = startResult.Value.Workspace.WorkspaceId;
        var statePath = Path.Combine(basePath, ".weave", "workspace-id");
        await File.WriteAllTextAsync(statePath, workspaceId, ct);
        CliTheme.WriteInfo($"  Workspace started (ID: {workspaceId}).");

        await RestoreSkillsAsync(export, workspaceId, ct);
        await RestoreChannelsAsync(export, workspaceId, ct);
    }

    private async Task RestoreSkillsAsync(WorkspaceExport export, string workspaceId, CancellationToken ct)
    {
        if (export.Skills.Count == 0)
            return;

        var restored = 0;
        var skillErrors = new List<string>();
        foreach (var skill in export.Skills)
        {
            var result = await postSkillAction.ExecuteAsync(new PostSkillInput(workspaceId, skill), ct);
            if (result.IsSuccess)
                restored++;
            else
                skillErrors.Add(result.Failure.Message);
        }

        CliTheme.WriteInfo($"  Skills restored: {restored}/{export.Skills.Count}");
        foreach (var err in skillErrors)
            CliTheme.WriteWarning($"    Skill failed: {err}");
    }

    private async Task RestoreChannelsAsync(WorkspaceExport export, string workspaceId, CancellationToken ct)
    {
        if (export.Channels.Count == 0)
            return;

        var restored = 0;
        var channelErrors = new List<string>();
        foreach (var channel in export.Channels)
        {
            var result = await postChannelAction.ExecuteAsync(new PostChannelInput(workspaceId, channel), ct);
            if (result.IsSuccess)
                restored++;
            else
                channelErrors.Add(result.Failure.Message);
        }

        CliTheme.WriteInfo($"  Channels restored: {restored}/{export.Channels.Count}");
        foreach (var err in channelErrors)
            CliTheme.WriteWarning($"    Channel failed: {err}");
    }
}
