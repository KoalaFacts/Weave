namespace Weave.Agents.Models;

public sealed record EpisodeSearchResult
{
    public required Episode Episode { get; init; }
    public required double RelevanceScore { get; init; }
}
