using System.Text.Json.Serialization;

namespace Weave.Workspaces.RuntimeRecovery;

[JsonConverter(typeof(JsonStringEnumConverter<WorkspaceRuntimeReconciliationOutcome>))]
public enum WorkspaceRuntimeReconciliationOutcome
{
    Blocked,
    Confirmed,
    PlanChanged,
    InvalidRequest,
    AlreadyAdmitted,
    OutcomeUnknown,
    EvidenceUnconfirmed
}
