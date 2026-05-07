using System.Text.Json;

namespace Weave.Cli.Commands;

internal sealed class DataExportCliCommand : ICliCommand<DataExportOptions>
{

    public string Name => "export";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Export a complete workspace snapshot to a portable JSON file";

    public async Task<int> ExecuteAsync(DataExportOptions options, CancellationToken ct)
    {
        var workspace = SelectWorkspace(options.Workspace);
        var manifestPath = ResolveManifestPath(workspace);
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

        using var client = new WorkspaceApiClient();
        using var marketplaceClient = new MarketplaceApiClient();
        if (!await client.IsReachableAsync(ct))
        {
            CliTheme.WriteError("Weave server is not running. Start it with 'weave run'.");
            return 1;
        }

        CliTheme.WriteInfo($"Exporting workspace '{workspace}'...");

        var export = await BuildExportAsync(workspace, manifestPath, client, marketplaceClient, ct);
        await WriteAsync(export, outputPath, ct);

        Spectre.Console.AnsiConsole.WriteLine();
        CliTheme.WriteSuccess($"Exported to {Path.GetFullPath(outputPath)}");
        CliTheme.WriteMuted($"  File size: {new FileInfo(outputPath).Length / 1024} KB");
        CliTheme.WriteMuted("  Import on another machine with: weave data import " + Path.GetFileName(outputPath));

        return 0;
    }

    private static string? SelectWorkspace(string? workspace)
        => WorkspacePrompt.SelectName(workspace, "Which workspace would you like to export?");

    private static string? ResolveManifestPath(string? workspace) => ManifestResolver.Resolve(workspace);

    private static async Task<WorkspaceExport> BuildExportAsync(
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

    private static async Task ExportLiveDataAsync(WorkspaceExport export, string manifestPath, WorkspaceApiClient client, CancellationToken ct)
    {
        var statePath = WorkspaceManifestPaths.GetStatePath(manifestPath);
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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or IOException)
        {
            CliTheme.WriteMuted($"  Agents: (not available: {ex.Message})");
        }

        try
        {
            var tools = await client.GetToolsAsync(workspaceId, ct);
            export.Tools = tools;
            CliTheme.WriteInfo($"  Tools: {tools.Count}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or IOException)
        {
            CliTheme.WriteMuted($"  Tools: (not available: {ex.Message})");
        }

        try
        {
            var skills = await client.GetSkillsAsync(workspaceId, ct);
            export.Skills = skills;
            CliTheme.WriteInfo($"  Skills: {skills.Count}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or IOException)
        {
            CliTheme.WriteMuted($"  Skills: (not available: {ex.Message})");
        }

        try
        {
            var channels = await client.GetChannelsAsync(workspaceId, ct);
            export.Channels = channels;
            CliTheme.WriteInfo($"  Channels: {channels.Count}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or IOException)
        {
            CliTheme.WriteMuted($"  Channels: (not available: {ex.Message})");
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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or IOException)
        {
            CliTheme.WriteMuted($"  Marketplace: (not available: {ex.Message})");
        }

        try
        {
            var templates = await client.GetTemplatesAsync(ct);
            export.Templates = templates;
            CliTheme.WriteInfo($"  Templates: {templates.Count}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or IOException)
        {
            CliTheme.WriteMuted($"  Templates: (not available: {ex.Message})");
        }
    }
}
