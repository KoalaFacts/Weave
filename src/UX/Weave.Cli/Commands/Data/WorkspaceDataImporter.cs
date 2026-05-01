using System.Text.Json;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is injected for CLI testability.")]
internal sealed class WorkspaceDataImporter
{
    public async Task<WorkspaceExport?> ReadAsync(string filePath, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(filePath, ct);
        return JsonSerializer.Deserialize(json, DataJsonContext.Default.WorkspaceExport);
    }

    public async Task<string> RestoreFilesAsync(WorkspaceExport export, string name, CancellationToken ct)
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

    public async Task TryStartImportedWorkspaceAsync(
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
        catch (Exception ex)
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
            catch (Exception ex)
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
