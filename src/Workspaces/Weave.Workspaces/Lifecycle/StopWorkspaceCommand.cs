using Weave.Shared.Ids;

namespace Weave.Workspaces.Lifecycle;

public sealed record StopWorkspaceCommand(WorkspaceId WorkspaceId);
