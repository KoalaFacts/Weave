namespace Weave.Agents.Memory;

public sealed record EpisodeSearchResult
{
    public required Episode Episode { get; init; }
    public required double RelevanceScore { get; init; }
}
