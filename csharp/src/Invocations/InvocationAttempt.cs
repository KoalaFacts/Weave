using Weave.Shared.Ids;

namespace Weave.Invocations;

public sealed record InvocationAttempt(
    InvocationAttemptId AttemptId,
    DateTimeOffset StartedAt,
    InvocationOutcome Outcome,
    DateTimeOffset? CompletedAt,
    TimeSpan Duration);
