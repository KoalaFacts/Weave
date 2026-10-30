namespace Weave.Invocations;

public sealed record InvocationClaim(bool Created, InvocationRecord Record)
{
    public InvocationApproval? Approval { get; init; }
    public string? BlockingReason { get; init; }
}
