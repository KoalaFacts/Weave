using Weave.Workspaces.Manifest;
namespace Weave.Cli.Shell;

internal static class WorkspaceManifestFile
{
    public static async Task<WorkspaceManifest> ReadAsync(string manifestPath, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(manifestPath, ct);
        return new ManifestParser().Parse(json);
    }

    public static async Task<WorkspaceManifest> ReadPreparedAsync(string manifestPath, CancellationToken ct)
    {
        var manifest = await ReadAsync(manifestPath, ct);
        var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? Directory.GetCurrentDirectory();
        return WorkspaceManifestPaths.PrepareForSilo(manifest, manifestDirectory);
    }

    public static async Task WriteAsync(string manifestPath, WorkspaceManifest manifest, CancellationToken ct)
    {
        var json = new ManifestParser().Serialize(manifest);
        await File.WriteAllTextAsync(manifestPath, json, ct);
    }
}
