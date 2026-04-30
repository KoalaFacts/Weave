using Weave.Shared.Ids;

namespace Weave.Workspaces.Runtime;

public sealed record WorkspaceEnvironment(
    WorkspaceId WorkspaceId,
    NetworkId NetworkId,
    IReadOnlyList<ContainerHandle> Containers);
