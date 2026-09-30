using System.Text.Json.Serialization;

namespace Weave.Workspaces.Runtime;

[JsonConverter(typeof(JsonStringEnumConverter<NetworkRuntimeCondition>))]
public enum NetworkRuntimeCondition
{
    Unknown,
    Present,
    Missing,
    Unavailable,
    Unsupported,
    InvalidIdentity,
    RuntimeMismatch,
    NotRecorded
}
