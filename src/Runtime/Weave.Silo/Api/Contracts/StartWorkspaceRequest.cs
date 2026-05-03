using Weave.Workspaces.Models;

namespace Weave.Silo.Api;

public sealed record StartWorkspaceRequest
{
    public required WorkspaceManifest Manifest { get; init; }
}
