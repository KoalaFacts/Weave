using System.Text.Json;
using Weave.Actions.Channel;
using Weave.Actions.Context;
using Weave.Actions.Marketplace;
using Weave.Actions.Skill;
using Weave.Actions.SystemInfo;
using Weave.Actions.Template;

namespace Weave.Cli.Commands;

internal sealed class DataExportCliCommand(
    IManifestResolver manifestResolver,
    WorkspacePrompt workspacePrompt,
    GetSystemInfoAction systemInfoAction,
    ListSkillsAction listSkillsAction,
    ListChannelsAction listChannelsAction,
    ListTemplatesAction listTemplatesAction,
    ListMarketplaceItemsAction listMarketplaceAction) : ICliCommand<DataExportOptions>
{
    public string Name => "export";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Export a complete workspace snapshot to a portable JSON file";

    public async Task<int> ExecuteAsync(DataExportOptions options, CancellationToken ct)
    {
        var workspace = workspacePrompt.SelectName(options.Workspace, "Which workspace would you like to export?");
        var manifestPath = manifestResolver.Resolve(workspace);
        if (manifestPath is null)
        {
            WorkspacePrompt.WriteManifestNotFound(workspace);
            return 1;
        }

        if (workspace is null)
        {
            var manifest = await WorkspaceManifestFile.ReadAsync(manifestPath, ct);
            workspace = manifest.Name;
        }

        var outputPath = options.Output ?? $"{workspace}-export.json";

        var systemInfo = await systemInfoAction.ExecuteAsync(new GetSystemInfoInput(), ct);
        if (!systemInfo.IsSuccess || !systemInfo.Value.Reachable)
        {
            CliTheme.WriteError("Weave server is not running. Start it with 'weave run'.");
            return 1;
        }

        CliTheme.WriteInfo($"Exporting workspace '{workspace}'...");

        var export = await BuildExportAsync(workspace, manifestPath, ct);
        await WriteAsync(export, outputPath, ct);

        Spectre.Console.AnsiConsole.WriteLine();
        CliTheme.WriteSuccess($"Exported to {Path.GetFullPath(outputPath)}");
        CliTheme.WriteMuted($"  File size: {new FileInfo(outputPath).Length / 1024} KB");
        CliTheme.WriteMuted("  Import on another machine with: weave data import " + Path.GetFileName(outputPath));

        return 0;
    }

    private async Task<WorkspaceExport> BuildExportAsync(
        string workspace,
        string manifestPath,
        CancellationToken ct)
    {
        var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var export = new WorkspaceExport
        {
            ExportedAt = DateTimeOffset.UtcNow,
            WeaveVersion = "1.0",
            WorkspaceName = workspace,
            Manifest = await File.ReadAllTextAsync(manifestPath, ct)
        };

        await ExportPromptFilesAsync(export, manifestDir, ct);
        await ExportLiveDataAsync(export, manifestPath, ct);
        await ExportGlobalDataAsync(export, ct);
        return export;
    }

    private static async Task WriteAsync(WorkspaceExport export, string outputPath, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(export, DataJsonContext.Default.WorkspaceExport);
        await File.WriteAllTextAsync(outputPath, json, ct);
    }

    private static async Task ExportPromptFilesAsync(WorkspaceExport export, string manifestDir, CancellationToken ct)
    {
        var promptsDir = Path.Combine(manifestDir, "prompts");
        if (!Directory.Exists(promptsDir))
            return;

        foreach (var file in Directory.GetFiles(promptsDir, "*.md"))
        {
            var name = Path.GetFileName(file);
            var content = await File.ReadAllTextAsync(file, ct);
            export.PromptFiles[name] = content;
        }
    }

    private async Task ExportLiveDataAsync(WorkspaceExport export, string manifestPath, CancellationToken ct)
    {
        var statePath = WorkspaceManifestPaths.GetStatePath(manifestPath);
        if (!File.Exists(statePath))
            return;

        var workspaceId = (await File.ReadAllTextAsync(statePath, ct)).Trim();
        if (string.IsNullOrWhiteSpace(workspaceId))
            return;

        export.WorkspaceId = workspaceId;

        var skills = await listSkillsAction.ExecuteAsync(new ListSkillsInput(workspaceId), ct);
        if (skills.IsSuccess)
        {
            export.Skills = skills.Value.Skills;
            CliTheme.WriteInfo($"  Skills: {skills.Value.Skills.Count}");
        }
        else if (skills.Failure.Reason != ActionFailureReason.Cancelled)
        {
            CliTheme.WriteMuted($"  Skills: (not available: {skills.Failure.Message})");
        }

        var channels = await listChannelsAction.ExecuteAsync(new ListChannelsInput(workspaceId), ct);
        if (channels.IsSuccess)
        {
            export.Channels = channels.Value.Channels;
            CliTheme.WriteInfo($"  Channels: {channels.Value.Channels.Count}");
        }
        else if (channels.Failure.Reason != ActionFailureReason.Cancelled)
        {
            CliTheme.WriteMuted($"  Channels: (not available: {channels.Failure.Message})");
        }
    }

    private async Task ExportGlobalDataAsync(WorkspaceExport export, CancellationToken ct)
    {
        var marketplace = await listMarketplaceAction.ExecuteAsync(new ListMarketplaceItemsInput(), ct);
        if (marketplace.IsSuccess)
        {
            export.MarketplaceItems = marketplace.Value.Items;
            CliTheme.WriteInfo($"  Marketplace items: {marketplace.Value.Items.Count}");
        }
        else if (marketplace.Failure.Reason != ActionFailureReason.Cancelled)
        {
            CliTheme.WriteMuted($"  Marketplace: (not available: {marketplace.Failure.Message})");
        }

        var templates = await listTemplatesAction.ExecuteAsync(new ListTemplatesInput(), ct);
        if (templates.IsSuccess)
        {
            export.Templates = templates.Value.Templates;
            CliTheme.WriteInfo($"  Templates: {templates.Value.Templates.Count}");
        }
        else if (templates.Failure.Reason != ActionFailureReason.Cancelled)
        {
            CliTheme.WriteMuted($"  Templates: (not available: {templates.Failure.Message})");
        }
    }
}
