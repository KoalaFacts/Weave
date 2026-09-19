using Weave.Invocations;

namespace Weave.Silo.Invocations;

internal sealed record ApprovalHttpStatus(
    InvocationId InvocationId, InvocationApprovalState ApprovalState, DateTimeOffset ExpiresAt);
