using Weave.Workspaces.Plugins;

namespace Weave.Silo.Api;

public sealed record ConnectPluginResponse
{
    public required PluginStatus Status { get; init; }
    public List<string> Warnings { get; init; } = [];
}
