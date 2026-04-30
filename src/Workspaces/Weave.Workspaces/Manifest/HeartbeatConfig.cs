namespace Weave.Workspaces.Models;

public sealed record HeartbeatConfig
{
    public required string Cron { get; init; }
    public List<string> Tasks { get; init; } = [];
}