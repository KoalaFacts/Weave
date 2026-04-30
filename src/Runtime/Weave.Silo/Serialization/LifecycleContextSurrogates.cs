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

[RegisterConverter]
public sealed class LifecycleContextSurrogateConverter
    : IConverter<LifecycleContext, LifecycleContextSurrogate>
{
    public LifecycleContext ConvertFromSurrogate(in LifecycleContextSurrogate s) => new()
    {
        WorkspaceId = s.WorkspaceId,
        AgentName = s.AgentName,
        ToolName = s.ToolName,
        Phase = s.Phase,
        Properties = s.Properties ?? []
    };

    public LifecycleContextSurrogate ConvertToSurrogate(in LifecycleContext v) => new()
    {
        WorkspaceId = v.WorkspaceId,
        AgentName = v.AgentName,
        ToolName = v.ToolName,
        Phase = v.Phase,
        Properties = v.Properties
    };
}
