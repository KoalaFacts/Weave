using Weave.Shared.Ids;

namespace Weave.Agents.Models;

public sealed record Episode
{
    public required EpisodeId EpisodeId { get; init; }
    public required string Title { get; init; }
    public required string Narrative { get; init; }
    public required string AgentName { get; init; }
    public List<string> Tags { get; init; } = [];
    public List<EpisodeDecision> Decisions { get; init; } = [];
    public string? SourceTaskId { get; init; }
    public string? ReviewFeedback { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public int RecallCount { get; set; }
    public DateTimeOffset? LastRecalledAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}
