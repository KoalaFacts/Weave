using Weave.Invocations;
using Weave.Shared.Ids;

namespace Weave.Tools.Tool;

public sealed record ToolResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = string.Empty;
    public string? Error { get; init; }
    public TimeSpan Duration { get; init; }
    public string ToolName { get; init; } = string.Empty;
    public InvocationId? InvocationId { get; init; }
    public InvocationAttemptId? AttemptId { get; init; }
    public InvocationOutcome? Outcome { get; init; }
    public bool OutcomeRecorded { get; init; }
    public bool IsReplay { get; init; }
    public string? ErrorCode { get; init; }
}
