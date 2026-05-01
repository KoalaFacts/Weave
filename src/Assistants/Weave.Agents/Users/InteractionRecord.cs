namespace Weave.Agents.Models;

public sealed record InteractionRecord
{
    public required string AgentName { get; init; }
    public required string Summary { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public List<string> Topics { get; init; } = [];
    public bool WasSatisfied { get; init; } = true;
}
