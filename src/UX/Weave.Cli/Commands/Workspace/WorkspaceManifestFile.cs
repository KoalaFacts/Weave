using Weave.Workspaces.Manifest;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is injected for CLI testability.")]
internal sealed class WorkspaceManifestFile
{
    public async Task<WorkspaceManifest> ReadAsync(string manifestPath, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(manifestPath, ct);
        return new ManifestParser().Parse(json);
    }

    public async Task<WorkspaceManifest> ReadPreparedAsync(string manifestPath, CancellationToken ct)
    {
        var manifest = await ReadAsync(manifestPath, ct);
        var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? Directory.GetCurrentDirectory();
        return WorkspaceApiClient.PrepareManifest(manifest, manifestDirectory);
    }

    public async Task WriteAsync(string manifestPath, WorkspaceManifest manifest, CancellationToken ct)
    {
        var json = new ManifestParser().Serialize(manifest);
        await File.WriteAllTextAsync(manifestPath, json, ct);
    }
}
