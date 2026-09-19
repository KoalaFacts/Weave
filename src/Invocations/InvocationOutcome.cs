namespace Weave.Invocations;

/// <summary>Persisted values are append-only. Unknown never permits automatic replay.</summary>
public enum InvocationOutcome
{
    OutcomeUnknown = 0,
    Succeeded = 1,
    Failed = 2,
    Cancelled = 3,
    Denied = 4,
    NotDispatched = 5
}
