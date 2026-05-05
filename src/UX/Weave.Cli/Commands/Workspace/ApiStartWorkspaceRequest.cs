using Weave.Workspaces.Manifest;
namespace Weave.Cli.Commands;

internal sealed record ApiStartWorkspaceRequest
{
    public required WorkspaceManifest Manifest { get; init; }
}
