using Microsoft.Extensions.Logging;
using Weave.Agents.Events;
using Weave.Agents.Models;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Grains;

public sealed class SkillMemoryGrain(
    IEventBus eventBus,
    TimeProvider timeProvider,
    ILogger<SkillMemoryGrain> logger,
    [PersistentState("skill-memory", "Default")] IPersistentState<SkillMemoryState> persistentState) : Grain, ISkillMemoryGrain
{
    public async Task<SkillDocument> StoreSkillAsync(SkillDocument skill)
    {
        var key = skill.SkillId.ToString();
        persistentState.State.Skills[key] = skill;
        await persistentState.WriteStateAsync();

        await eventBus.PublishAsync(new SkillCreatedEvent
        {
            SourceId = key,
            WorkspaceId = WorkspaceId.From(persistentState.State.WorkspaceId),
            SkillId = skill.SkillId,
            Title = skill.Title,
            CreatedByAgent = skill.CreatedByAgent
        }, CancellationToken.None);

        logger.LogInformation("Skill {SkillId} ({Title}) stored in workspace {WorkspaceId}",
            skill.SkillId, skill.Title, persistentState.State.WorkspaceId);

        return skill;
    }

    public Task<IReadOnlyList<SkillSearchResult>> SearchAsync(string query, int maxResults = 5)
    {
        if (persistentState.State.Skills.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<SkillSearchResult>>(new List<SkillSearchResult>());
        }

        var queryTokens = Tokenize(query);
        if (queryTokens.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<SkillSearchResult>>(new List<SkillSearchResult>());
        }

        var scored = new List<SkillSearchResult>();

        foreach (var skill in persistentState.State.Skills.Values)
        {
            var score = ComputeRelevanceScore(skill, queryTokens);
            if (score > 0)
                scored.Add(new SkillSearchResult { Skill = skill, RelevanceScore = score });
        }

        IReadOnlyList<SkillSearchResult> results = scored
            .OrderByDescending(r => r.RelevanceScore)
            .Take(maxResults)
            .ToList();

        return Task.FromResult(results);
    }

    public Task<SkillDocument?> GetSkillAsync(SkillId skillId)
    {
        persistentState.State.Skills.TryGetValue(skillId.ToString(), out var skill);
        return Task.FromResult(skill);
    }

    public async Task RecordUsageAsync(SkillId skillId, bool success)
    {
        var key = skillId.ToString();
        if (!persistentState.State.Skills.TryGetValue(key, out var skill))
            return;

        skill.UseCount++;
        skill.LastUsedAt = timeProvider.GetUtcNow();
        skill.SuccessRate = (skill.SuccessRate * (skill.UseCount - 1) + (success ? 1.0 : 0.0)) / skill.UseCount;

        await persistentState.WriteStateAsync();
    }

    public Task<IReadOnlyList<SkillDocument>> GetAllSkillsAsync()
    {
        IReadOnlyList<SkillDocument> skills = persistentState.State.Skills.Values.ToList();
        return Task.FromResult(skills);
    }

    public async Task RemoveSkillAsync(SkillId skillId)
    {
        var key = skillId.ToString();
        if (persistentState.State.Skills.Remove(key))
        {
            await persistentState.WriteStateAsync();
            logger.LogInformation("Skill {SkillId} removed from workspace {WorkspaceId}",
                skillId, persistentState.State.WorkspaceId);
        }
    }

    internal static double ComputeRelevanceScore(SkillDocument skill, string[] queryTokens)
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

        // Boost by usage (log2) and success rate
        if (skill.UseCount > 0)
            score += Math.Log2(skill.UseCount + 1);

        score *= skill.SuccessRate;

        return score;
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
