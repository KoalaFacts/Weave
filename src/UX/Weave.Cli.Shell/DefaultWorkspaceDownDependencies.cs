using Weave.Actions.Context;
using Weave.Actions.Workspace;

namespace Weave.Cli.Shell;

internal sealed class DefaultWorkspaceDownDependencies(StopWorkspaceAction stopAction, IManifestResolver manifestResolver) : IWorkspaceDownDependencies
{
    public string? ResolveManifestPath(string? name) => manifestResolver.Resolve(name);

    public string GetWorkspaceStatePath(string manifestPath) => WorkspaceManifestPaths.GetStatePath(manifestPath);

    public bool FileExists(string path) => File.Exists(path);

    public Task<string> ReadAllTextAsync(string path, CancellationToken ct) => File.ReadAllTextAsync(path, ct);

    public Task<ActionResult<StopWorkspaceResult>> StopWorkspaceAsync(string workspaceId, CancellationToken ct) =>
        stopAction.ExecuteAsync(new StopWorkspaceInput(workspaceId), ct);

    public void DeleteFile(string path) => File.Delete(path);
}
