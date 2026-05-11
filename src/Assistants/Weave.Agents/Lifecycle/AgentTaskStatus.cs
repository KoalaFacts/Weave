namespace Weave.Agents.Lifecycle;

public enum AgentTaskStatus
{
    Pending,
    Running,
    AwaitingReview,
    Accepted,
    Rejected,
    Completed,
    Failed,
    Cancelled
}
