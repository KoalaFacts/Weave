using Weave.Shared.Ids;

namespace Weave.Invocations;

/// <summary>
/// Immutable intent and local authorization evidence. Subject is the validated
/// token's IssuedTo, not a newly implemented portable Agent ID. No payloads or credentials.
/// </summary>
public sealed record InvocationRecord(
    InvocationId InvocationId,
    string WorkspaceId,
    string Subject,
    string ToolName,
    string Operation,
    string InputDigest,
    string TokenId,
    string AuthorizedGrant,
    DateTimeOffset CreatedAt,
    InvocationAttempt Attempt)
{
    public string? ApprovalPlanDigest { get; init; }
}
