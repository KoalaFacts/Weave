using Weave.Shared.Ids;

namespace Weave.Workspaces.Models;

public sealed record ContainerInfo
{
    public ContainerId ContainerId { get; init; } = ContainerId.Empty;
    public string Name { get; init; } = string.Empty;
    public string Image { get; init; } = string.Empty;
    public ContainerStatus Status { get; init; } = ContainerStatus.Stopped;
}
