using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Runtime;

public sealed record ContainerSpec
{
    public required string Name { get; init; }
    public required string Image { get; init; }
    public Dictionary<string, string> Environment { get; init; } = [];
    public Dictionary<int, int> PortMappings { get; init; } = [];
    public List<MountConfig> Mounts { get; init; } = [];
    public NetworkId? NetworkId { get; init; }
    public List<string> Command { get; init; } = [];
    public bool ReadOnly { get; init; }
    public bool DropAllCapabilities { get; init; } = true;
    public bool NoNetwork { get; init; }
}
