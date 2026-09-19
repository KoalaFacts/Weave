namespace Weave.Invocations;

/// <summary>Owned, verified display data at review time. Not an approval or a dispatch credential.</summary>
public sealed record InvocationApprovalReview(
    InvocationId InvocationId,
    string WorkspaceId,
    string Subject,
    string ToolName,
    string Operation,
    string TargetDescription,
    Dictionary<string, string> Parameters,
    string? RawInput,
    string PlanDigest,
    DateTimeOffset ExpiresAt);
