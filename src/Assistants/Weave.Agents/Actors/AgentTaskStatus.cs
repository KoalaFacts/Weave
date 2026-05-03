namespace Weave.Agents.Models;

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
