using System.Text.Json.Serialization;

namespace Weave.Silo.Invocations;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record StoredApprovalDecisionRequest
{
    public string Decision { get; init; } = string.Empty;
    public string PlanDigest { get; init; } = string.Empty;
}
