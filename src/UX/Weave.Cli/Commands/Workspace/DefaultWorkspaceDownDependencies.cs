using Weave.Actions.Context;
using Weave.Actions.Workspace;

namespace Weave.Cli.Commands;

internal sealed class DefaultWorkspaceDownDependencies : IWorkspaceDownDependencies
{
    private readonly StopWorkspaceAction _stopAction;

    public DefaultWorkspaceDownDependencies(StopWorkspaceAction stopAction)
    {
        _stopAction = stopAction;
    }

    public string? ResolveManifestPath(string? name) => ManifestResolver.Resolve(name);

    public string GetWorkspaceStatePath(string manifestPath) => WorkspaceApiClient.GetWorkspaceStatePath(manifestPath);

    public bool FileExists(string path) => File.Exists(path);

    public Task<string> ReadAllTextAsync(string path, CancellationToken ct) => File.ReadAllTextAsync(path, ct);

    public async Task StopWorkspaceAsync(string workspaceId, CancellationToken ct)
    {
        var result = await _stopAction.ExecuteAsync(new StopWorkspaceInput(workspaceId), ct);
        if (result.IsSuccess)
            return;

        // Translate the typed failure back into an exception so the calling
        // CLI command keeps its existing catch shape; the message carries the
        // silo's detail (Conflict/SiloUnreachable/etc.) verbatim.
        throw new InvalidOperationException(result.Failure.Message);
    }

    public void DeleteFile(string path) => File.Delete(path);
}
