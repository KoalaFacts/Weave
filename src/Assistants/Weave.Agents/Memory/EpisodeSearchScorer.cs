using Weave.Agents.Models;
using Weave.Agents.Skills;

namespace Weave.Agents.Memory;

internal static class EpisodeSearchScorer
{
    internal static double ComputeRelevanceScore(
        Episode episode,
        string[] queryTokens,
        EpisodeSearchOptions options,
        DateTimeOffset now)
    {
        var tagTokens = episode.Tags.SelectMany(SkillSearchScorer.Tokenize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var titleTokens = new HashSet<string>(SkillSearchScorer.Tokenize(episode.Title), StringComparer.OrdinalIgnoreCase);
        var narrativeTokens = new HashSet<string>(SkillSearchScorer.Tokenize(episode.Narrative), StringComparer.OrdinalIgnoreCase);

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
            score += SkillSearchScorer.ComputeRecencyBoost(now, anchor);
        }

        return score;
    }
}
