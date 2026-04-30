using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal sealed record ApiStartWorkspaceRequest
{
    public required WorkspaceManifest Manifest { get; init; }
}
