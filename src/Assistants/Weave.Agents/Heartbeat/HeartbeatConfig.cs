namespace Weave.Agents.Heartbeat;

public sealed record HeartbeatConfig
{
    public string Cron { get; init; } = "*/30 * * * *";
    public List<string> Tasks { get; init; } = [];
    public bool Enabled { get; init; } = true;
}
public sealed record HeartbeatState
{
    public bool IsRunning { get; init; }
    public DateTimeOffset? LastRun { get; init; }
    public DateTimeOffset? NextRun { get; init; }
    public int ExecutionCount { get; init; }
    public HeartbeatConfig Config { get; init; } = new();
}
