using Weave.Invocations;
using Weave.Tools.Tool;

namespace Weave.Silo.Invocations;

internal sealed record InvocationHttpResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = string.Empty;
    public string? Error { get; init; }
    public TimeSpan Duration { get; init; }
    public string ToolName { get; init; } = string.Empty;
    public string? InvocationId { get; init; }
    public string? AttemptId { get; init; }
    public InvocationOutcome? Outcome { get; init; }
    public bool OutcomeRecorded { get; init; }
    public bool IsReplay { get; init; }
    public string? ErrorCode { get; init; }
    public InvocationApprovalState? ApprovalState { get; init; }
    public string? ApprovalPlanDigest { get; init; }
    public DateTimeOffset? ApprovalExpiresAt { get; init; }

    public static InvocationHttpResult FromResult(ToolResult result) => new()
    {
        Success = result.Success,
        Output = result.Output,
        Error = result.Error,
        Duration = result.Duration,
        ToolName = result.ToolName,
        InvocationId = result.InvocationId?.ToString(),
        AttemptId = result.AttemptId?.ToString(),
        Outcome = result.Outcome,
        OutcomeRecorded = result.OutcomeRecorded,
        IsReplay = result.IsReplay,
        ErrorCode = result.ErrorCode,
        ApprovalState = result.ApprovalState,
        ApprovalPlanDigest = result.ApprovalPlanDigest,
        ApprovalExpiresAt = result.ApprovalExpiresAt
    };
}
