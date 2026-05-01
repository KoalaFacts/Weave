using Weave.Shared.Lifecycle;

namespace Weave.Silo.Serialization;

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