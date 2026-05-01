using System.Text.Json;

namespace Weave.Cli.Commands;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is injected for CLI testability.")]
internal sealed class WorkspaceDataExporter
{
    public async Task<WorkspaceExport> BuildExportAsync(
        string workspace,
        string manifestPath,
        WorkspaceApiClient client,
        MarketplaceApiClient marketplaceClient,
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
        await ExportLiveDataAsync(export, manifestPath, client, ct);
        await ExportGlobalDataAsync(export, marketplaceClient, client, ct);
        return export;
    }

    public async Task WriteAsync(WorkspaceExport export, string outputPath, CancellationToken ct)
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

    private static async Task ExportLiveDataAsync(WorkspaceExport export, string manifestPath, WorkspaceApiClient client, CancellationToken ct)
    {
        var statePath = WorkspaceApiClient.GetWorkspaceStatePath(manifestPath);
        var workspaceId = File.Exists(statePath)
            ? (await File.ReadAllTextAsync(statePath, ct)).Trim()
            : null;

        if (workspaceId is null)
            return;

        export.WorkspaceId = workspaceId;
        await TryExportLiveDataAsync(client, export, workspaceId, ct);
    }

    private static async Task TryExportLiveDataAsync(WorkspaceApiClient client, WorkspaceExport export, string workspaceId, CancellationToken ct)
    {
        try
        {
            var agents = await client.GetAgentsAsync(workspaceId, ct);
            export.Agents = agents;
            CliTheme.WriteInfo($"  Agents: {agents.Count}");
        }
        catch
        {
            CliTheme.WriteMuted("  Agents: (not available)");
        }

        try
        {
            var tools = await client.GetToolsAsync(workspaceId, ct);
            export.Tools = tools;
            CliTheme.WriteInfo($"  Tools: {tools.Count}");
        }
        catch
        {
            CliTheme.WriteMuted("  Tools: (not available)");
        }

        try
        {
            var skills = await client.GetSkillsAsync(workspaceId, ct);
            export.Skills = skills;
            CliTheme.WriteInfo($"  Skills: {skills.Count}");
        }
        catch
        {
            CliTheme.WriteMuted("  Skills: (not available)");
        }

        try
        {
            var channels = await client.GetChannelsAsync(workspaceId, ct);
            export.Channels = channels;
            CliTheme.WriteInfo($"  Channels: {channels.Count}");
        }
        catch
        {
            CliTheme.WriteMuted("  Channels: (not available)");
        }
    }

    private static async Task ExportGlobalDataAsync(WorkspaceExport export, MarketplaceApiClient marketplaceClient, WorkspaceApiClient client, CancellationToken ct)
    {
        try
        {
            var marketplace = await marketplaceClient.GetItemsAsync(ct);
            export.MarketplaceItems = marketplace;
            CliTheme.WriteInfo($"  Marketplace items: {marketplace.Count}");
        }
        catch
        {
            CliTheme.WriteMuted("  Marketplace: (not available)");
        }

        try
        {
            var templates = await client.GetTemplatesAsync(ct);
            export.Templates = templates;
            CliTheme.WriteInfo($"  Templates: {templates.Count}");
        }
        catch
        {
            CliTheme.WriteMuted("  Templates: (not available)");
        }
    }
}
