using Weave.Shared.Ids;

namespace Weave.Workspaces.Runtime;

public sealed record ContainerHandle(
    ContainerId ContainerId,
    string Name,
    string Image,
    IReadOnlyDictionary<int, int> PortMappings);
