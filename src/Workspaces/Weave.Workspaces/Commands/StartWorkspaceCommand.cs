using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Commands;

public sealed record StartWorkspaceCommand(WorkspaceId WorkspaceId, WorkspaceManifest Manifest);
