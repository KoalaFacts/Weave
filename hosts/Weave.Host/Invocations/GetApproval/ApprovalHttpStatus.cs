using Weave.Invocations;

namespace Weave.Silo.Invocations;

internal sealed record ApprovalHttpStatus(
    string InvocationId, InvocationApprovalState ApprovalState, DateTimeOffset ExpiresAt);
