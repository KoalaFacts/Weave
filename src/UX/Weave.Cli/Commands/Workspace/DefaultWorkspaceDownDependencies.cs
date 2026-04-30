namespace Weave.Cli.Commands;

internal sealed class DefaultWorkspaceDownDependencies : IWorkspaceDownDependencies
{
    public string? ResolveManifestPath(string? name) => ManifestResolver.Resolve(name);

    public string GetWorkspaceStatePath(string manifestPath) => WorkspaceApiClient.GetWorkspaceStatePath(manifestPath);

    public bool FileExists(string path) => File.Exists(path);

    public Task<string> ReadAllTextAsync(string path, CancellationToken ct) => File.ReadAllTextAsync(path, ct);

    public async Task StopWorkspaceAsync(string workspaceId, CancellationToken ct)
    {
        using var client = new WorkspaceApiClient();
        await client.StopWorkspaceAsync(workspaceId, ct);
    }

    public void DeleteFile(string path) => File.Delete(path);
}
