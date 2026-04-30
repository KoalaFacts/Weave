namespace Weave.Agents.Heartbeat;

public sealed record HeartbeatState
{
    public bool IsRunning { get; init; }
    public DateTimeOffset? LastRun { get; init; }
    public DateTimeOffset? NextRun { get; init; }
    public int ExecutionCount { get; init; }
    public HeartbeatConfig Config { get; init; } = new();
}