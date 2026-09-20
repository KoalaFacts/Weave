namespace Weave.Dashboard.Approvals;

public sealed record ApprovalReviewSnapshot
{
    public string InvocationId { get; init; } = string.Empty;
    public string WorkspaceId { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string ToolName { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public string TargetDescription { get; init; } = string.Empty;
    public Dictionary<string, string> Parameters { get; init; } = [];
    public string? RawInput { get; init; }
    public string PlanDigest { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; init; }
}
