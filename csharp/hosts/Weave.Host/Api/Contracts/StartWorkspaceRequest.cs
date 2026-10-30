using Weave.Workspaces.Manifest;
namespace Weave.Silo.Api;

public sealed record StartWorkspaceRequest
{
    public required WorkspaceManifest Manifest { get; init; }
}
