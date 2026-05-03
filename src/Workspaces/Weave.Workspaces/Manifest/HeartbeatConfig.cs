namespace Weave.Workspaces.Models;

public sealed record HeartbeatConfig
{
    public required string Cron { get; init; }
    public IReadOnlyList<string> Tasks { get; init; } = [];
}
