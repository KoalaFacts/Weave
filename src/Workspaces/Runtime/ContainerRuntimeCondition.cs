using System.Text.Json.Serialization;

namespace Weave.Workspaces.Runtime;

[JsonConverter(typeof(JsonStringEnumConverter<ContainerRuntimeCondition>))]
public enum ContainerRuntimeCondition
{
    Unknown,
    Running,
    Stopped,
    Missing,
    Transitioning,
    Unavailable,
    Unsupported,
    InvalidIdentity,
    RuntimeMismatch,
    WorkspaceNotRunning
}
