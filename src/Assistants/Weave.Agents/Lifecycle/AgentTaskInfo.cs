using Weave.Shared.Ids;

using Weave.Agents.Verification;
namespace Weave.Agents.Lifecycle;

public sealed record AgentTaskInfo
{
    public required AgentTaskId TaskId { get; init; }
    public required string Description { get; init; }
    public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Pending;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ResultSummary { get; set; }
    public ProofOfWork? Proof { get; set; }
}
