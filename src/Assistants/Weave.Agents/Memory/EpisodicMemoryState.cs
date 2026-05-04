namespace Weave.Agents.Models;

public sealed record EpisodicMemoryState
{
    public Dictionary<string, Episode> Episodes { get; init; } = [];
    public string WorkspaceId { get; set; } = string.Empty;

    public IReadOnlyList<EpisodeSearchResult> Recall(string query, int maxResults, EpisodeSearchOptions? options, DateTimeOffset now)
    {
        var queryTokens = Tokenize(query);
        if (queryTokens.Length == 0)
            return [];

        var effectiveOptions = options ?? new EpisodeSearchOptions();
        var scored = new List<EpisodeSearchResult>();

        foreach (var episode in Episodes.Values)
        {
            if (episode.ArchivedAt is not null)
                continue;
            if (effectiveOptions.AgentName is { } agentName && !string.Equals(episode.AgentName, agentName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (effectiveOptions.Tag is { } tag && !episode.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                continue;
            if (effectiveOptions.Since is { } since && episode.OccurredAt < since)
                continue;

            var score = Score(episode, queryTokens, effectiveOptions, now);
            if (score > 0)
                scored.Add(new EpisodeSearchResult { Episode = episode, RelevanceScore = score });
        }

        return scored
            .OrderByDescending(r => r.RelevanceScore)
            .Take(maxResults)
            .ToList();
    }

    private static double Score(Episode episode, string[] queryTokens, EpisodeSearchOptions options, DateTimeOffset now)
    {
        var tagTokens = episode.Tags.SelectMany(Tokenize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var titleTokens = new HashSet<string>(Tokenize(episode.Title), StringComparer.OrdinalIgnoreCase);
        var narrativeTokens = new HashSet<string>(Tokenize(episode.Narrative), StringComparer.OrdinalIgnoreCase);

        double score = 0;
        foreach (var token in queryTokens)
        {
            if (tagTokens.Contains(token))
                score += 3;
            if (titleTokens.Contains(token))
                score += 2;
            if (narrativeTokens.Contains(token))
                score += 1;
        }

        if (score <= 0)
            return 0;

        if (episode.RecallCount > 0)
            score += Math.Log2(episode.RecallCount + 1);

        if (options.PreferRecent)
        {
            var anchor = episode.LastRecalledAt ?? episode.OccurredAt;
            var age = now - anchor;
            if (age >= TimeSpan.Zero)
            {
                if (age <= TimeSpan.FromDays(7))
                    score += 2.0;
                else if (age <= TimeSpan.FromDays(30))
                    score += 1.0;
            }
        }

        return score;
    }

    private static string[] Tokenize(string text) =>
        text.Split([' ', ',', '.', ';', ':', '-', '_', '/', '\\', '(', ')', '[', ']'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToArray();
}
