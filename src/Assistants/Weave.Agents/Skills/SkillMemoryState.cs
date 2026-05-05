namespace Weave.Agents.Models;

public sealed record SkillMemoryState
{
    public Dictionary<string, SkillDocument> Skills { get; init; } = [];
    public Dictionary<string, SkillSuggestion> SuggestedSkills { get; init; } = [];
    public string WorkspaceId { get; set; } = string.Empty;

    public IReadOnlyList<SkillSearchResult> Search(string query, int maxResults, SkillSearchOptions? options, DateTimeOffset now)
    {
        var queryTokens = Tokenize(query);
        if (queryTokens.Length == 0)
            return [];

        var effectiveOptions = options ?? new SkillSearchOptions();
        var minSuccessRate = Math.Clamp(effectiveOptions.MinSuccessRate, 0, 1);
        var scored = new List<SkillSearchResult>();

        foreach (var skill in Skills.Values)
        {
            if (skill.ArchivedAt is not null)
                continue;
            if (skill.SuccessRate < minSuccessRate)
                continue;

            var score = Score(skill, queryTokens, effectiveOptions, now);
            if (score > 0)
                scored.Add(new SkillSearchResult { Skill = skill, RelevanceScore = score });
        }

        return scored
            .OrderByDescending(result => result.RelevanceScore)
            .Take(maxResults)
            .ToList();
    }

    private static double Score(SkillDocument skill, string[] queryTokens, SkillSearchOptions options, DateTimeOffset now)
    {
        var tagTokens = skill.Tags.SelectMany(Tokenize).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
        {
            var age = now - lastUsedAt;
            if (age >= TimeSpan.Zero)
            {
                if (age <= TimeSpan.FromDays(7))
                    score += 2.0;
                else if (age <= TimeSpan.FromDays(30))
                    score += 1.0;
            }
        }

        score *= skill.SuccessRate;
        return score;
    }

    private static string[] Tokenize(string text) =>
        text.Split([' ', ',', '.', ';', ':', '-', '_', '/', '\\', '(', ')', '[', ']'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToArray();
}
