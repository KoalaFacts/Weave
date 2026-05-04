using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Lifecycle;

public sealed record StartWorkspaceCommand(WorkspaceId WorkspaceId, WorkspaceManifest Manifest);
