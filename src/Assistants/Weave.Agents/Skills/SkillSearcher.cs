using Weave.Agents.Models;

namespace Weave.Agents.Skills;

internal static class SkillSearcher
{
    internal static List<SkillSearchResult> Rank(
        IEnumerable<SkillDocument> skills,
        string query,
        int maxResults,
        SkillSearchOptions? options,
        DateTimeOffset now)
    {
        var queryTokens = SkillSearchScorer.Tokenize(query);
        if (queryTokens.Length == 0)
            return [];

        var scored = new List<SkillSearchResult>();
        var effectiveOptions = options ?? new SkillSearchOptions();
        var minSuccessRate = Math.Clamp(effectiveOptions.MinSuccessRate, 0, 1);

        foreach (var skill in skills)
        {
            if (skill.ArchivedAt is not null)
                continue;
            if (skill.SuccessRate < minSuccessRate)
                continue;

            var score = SkillSearchScorer.ComputeRelevanceScore(skill, queryTokens, effectiveOptions, now);
            if (score > 0)
                scored.Add(new SkillSearchResult { Skill = skill, RelevanceScore = score });
        }

        return scored
            .OrderByDescending(result => result.RelevanceScore)
            .Take(maxResults)
            .ToList();
    }
}
