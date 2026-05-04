using Weave.Agents.Models;

namespace Weave.Agents.Skills;

internal static class SkillSearchScorer
{
    internal static double ComputeRelevanceScore(
        SkillDocument skill,
        string[] queryTokens,
        SkillSearchOptions options,
        DateTimeOffset now)
    {
        var tagTokens = skill.Tags.SelectMany(t => Tokenize(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var titleTokens = new HashSet<string>(Tokenize(skill.Title), StringComparer.OrdinalIgnoreCase);
        var descriptionTokens = new HashSet<string>(Tokenize(skill.Description), StringComparer.OrdinalIgnoreCase);

        double score = 0;

        foreach (var token in queryTokens)
        {
            if (tagTokens.Contains(token))
                score += 3;
            if (titleTokens.Contains(token))
                score += 2;
            if (descriptionTokens.Contains(token))
                score += 1;
        }

        if (score <= 0)
            return 0;

        if (skill.UseCount > 0)
            score += Math.Log2(skill.UseCount + 1);

        if (options.PreferRecent && skill.LastUsedAt is { } lastUsedAt)
            score += ComputeRecencyBoost(now, lastUsedAt);

        score *= skill.SuccessRate;

        return score;
    }

    internal static double ComputeRecencyBoost(DateTimeOffset now, DateTimeOffset lastUsedAt)
    {
        var age = now - lastUsedAt;
        if (age < TimeSpan.Zero)
            return 0;
        if (age <= TimeSpan.FromDays(7))
            return 2.0;
        if (age <= TimeSpan.FromDays(30))
            return 1.0;

        return 0;
    }

    internal static string[] Tokenize(string text)
    {
        return text.Split([' ', ',', '.', ';', ':', '-', '_', '/', '\\', '(', ')', '[', ']'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToArray();
    }
}
