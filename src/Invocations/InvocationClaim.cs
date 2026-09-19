namespace Weave.Invocations;

public sealed record InvocationClaim(bool Created, InvocationRecord Record)
{
    public string? Rejection { get; init; }
}
