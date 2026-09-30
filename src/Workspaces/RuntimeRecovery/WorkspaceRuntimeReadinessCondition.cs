using System.Text.Json.Serialization;

namespace Weave.Workspaces.RuntimeRecovery;

[JsonConverter(typeof(JsonStringEnumConverter<WorkspaceRuntimeReadinessCondition>))]
public enum WorkspaceRuntimeReadinessCondition
{
    Unknown,
    Ready,
    NotReady
}
