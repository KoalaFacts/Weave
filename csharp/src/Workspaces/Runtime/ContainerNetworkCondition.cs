using System.Text.Json.Serialization;

namespace Weave.Workspaces.Runtime;

[JsonConverter(typeof(JsonStringEnumConverter<ContainerNetworkCondition>))]
public enum ContainerNetworkCondition
{
    NotChecked,
    Attached,
    Detached,
    Unknown,
    Unavailable,
    Unsupported,
    InvalidIdentity
}
