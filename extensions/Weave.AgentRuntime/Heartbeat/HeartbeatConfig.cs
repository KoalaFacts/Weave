namespace Weave.Agents.Heartbeat;

public sealed record HeartbeatConfig
{
    public string Cron { get; init; } = "*/30 * * * *";
    public IReadOnlyList<string> Tasks { get; init; } = [];
    public bool Enabled { get; init; } = true;
}
