using Weave.Actions.Context;
using Weave.Actions.Workspace;

namespace Weave.Cli.Shell;

internal interface IWorkspaceDownDependencies
{
    string? ResolveManifestPath(string? name);

    string GetWorkspaceStatePath(string manifestPath);

    bool FileExists(string path);

    Task<string> ReadAllTextAsync(string path, CancellationToken ct);

    Task<ActionResult<StopWorkspaceResult>> StopWorkspaceAsync(string workspaceId, CancellationToken ct);

    void DeleteFile(string path);
}
