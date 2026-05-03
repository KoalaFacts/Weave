using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;

namespace Weave.Silo.Serialization;

[GenerateSerializer]
public struct LifecycleContextSurrogate
{
    [Id(0)] public WorkspaceId WorkspaceId { get; set; }
    [Id(1)] public string? AgentName { get; set; }
    [Id(2)] public string? ToolName { get; set; }
    [Id(3)] public LifecyclePhase Phase { get; set; }
    [Id(4)] public Dictionary<string, string> Properties { get; set; }
}
