namespace Weave.Cli.Commands;

internal interface IWorkspaceDownDependencies
{
    string? ResolveManifestPath(string? name);

    string GetWorkspaceStatePath(string manifestPath);

    bool FileExists(string path);

    Task<string> ReadAllTextAsync(string path, CancellationToken ct);

    Task StopWorkspaceAsync(string workspaceId, CancellationToken ct);

    void DeleteFile(string path);
}
