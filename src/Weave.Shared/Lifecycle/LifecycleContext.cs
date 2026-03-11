using Orleans;
using Weave.Shared.Ids;

namespace Weave.Shared.Lifecycle;

[GenerateSerializer]
public sealed record LifecycleContext
{
    [Id(0)] public required WorkspaceId WorkspaceId { get; init; }
    [Id(1)] public string? AgentName { get; init; }
    [Id(2)] public string? ToolName { get; init; }
    [Id(3)] public LifecyclePhase Phase { get; init; }
    [Id(4)] public Dictionary<string, string> Properties { get; init; } = [];
}
