using Orleans;

namespace Weave.Agents.Heartbeat;

[GenerateSerializer]
public sealed record HeartbeatConfig
{
    [Id(0)] public string Cron { get; init; } = "*/30 * * * *";
    [Id(1)] public List<string> Tasks { get; init; } = [];
    [Id(2)] public bool Enabled { get; init; } = true;
}

[GenerateSerializer]
public sealed record HeartbeatState
{
    [Id(0)] public bool IsRunning { get; init; }
    [Id(1)] public DateTimeOffset? LastRun { get; init; }
    [Id(2)] public DateTimeOffset? NextRun { get; init; }
    [Id(3)] public int ExecutionCount { get; init; }
    [Id(4)] public HeartbeatConfig Config { get; init; } = new();
}
