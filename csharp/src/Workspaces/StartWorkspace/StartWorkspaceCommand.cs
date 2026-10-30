using Weave.Shared.Ids;
using Weave.Workspaces.Manifest;
namespace Weave.Workspaces.Lifecycle;

public sealed record StartWorkspaceCommand(WorkspaceId WorkspaceId, WorkspaceManifest Manifest);
