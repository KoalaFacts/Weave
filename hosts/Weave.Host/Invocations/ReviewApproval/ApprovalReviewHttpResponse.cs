using System.Collections.Immutable;
using Weave.Invocations;

namespace Weave.Silo.Invocations;

internal sealed record ApprovalReviewHttpResponse(
    string InvocationId,
    string WorkspaceId,
    string Subject,
    string ToolName,
    string Operation,
    string TargetDescription,
    ImmutableDictionary<string, string> Parameters,
    string? RawInput,
    string PlanDigest,
    DateTimeOffset ExpiresAt)
{
    public static ApprovalReviewHttpResponse FromReview(InvocationApprovalReview review) => new(
        review.InvocationId.ToString(), review.WorkspaceId, review.Subject, review.ToolName,
        review.Operation, review.TargetDescription, review.Parameters, review.RawInput, review.PlanDigest, review.ExpiresAt);
}
