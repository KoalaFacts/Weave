using System.Text.Json.Serialization;

namespace Weave.Workspaces.Runtime;

[JsonConverter(typeof(JsonStringEnumConverter<ContainerRecoveryOutcome>))]
public enum ContainerRecoveryOutcome
{
    Blocked,
    AlreadyRunning,
    Started,
    OutcomeUnknown,
    AlreadyAdmitted,
    EvidenceUnconfirmed
}
