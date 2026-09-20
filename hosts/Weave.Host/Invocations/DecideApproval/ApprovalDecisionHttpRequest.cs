namespace Weave.Silo.Invocations;

internal sealed record ApprovalDecisionHttpRequest(
    InvokeToolHttpRequest? Invocation,
    string? PlanDigest,
    string? Decision);
