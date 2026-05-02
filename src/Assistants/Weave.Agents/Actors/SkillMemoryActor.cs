using Microsoft.Extensions.Logging;
using Weave.Agents.Events;
using Weave.Agents.Models;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Actors;

public sealed class SkillMemoryActor(
    IEventBus eventBus,
    TimeProvider timeProvider,
    ICapabilityTokenService tokenService,
    ILogger<SkillMemoryActor> logger,
    IActorState<SkillMemoryState> persistentState) : ISkillMemoryActor
{
    private const string SkillRead = "skill:read";
    private const string SkillWrite = "skill:write";

    public async Task<SkillDocument> StoreSkillAsync(SkillDocument skill, CapabilityToken token)
    {
        Authorize(token, SkillWrite);

        var key = skill.SkillId.ToString();
        persistentState.State.Skills[key] = skill;
        await persistentState.WriteStateAsync();

        await PublishSkillCreatedAsync(skill, key);

        logger.LogInformation("Skill {SkillId} ({Title}) stored in workspace {WorkspaceId}",
            skill.SkillId, skill.Title, persistentState.State.WorkspaceId);

        return skill;
    }

    public async Task<SkillSuggestion> SuggestSkillAsync(SkillDocument skill, CapabilityToken token, string? sourceTaskId = null)
    {
        Authorize(token, SkillWrite);

        var key = skill.SkillId.ToString();
        var suggestion = new SkillSuggestion
        {
            Skill = skill,
            SourceTaskId = sourceTaskId,
            SuggestedAt = timeProvider.GetUtcNow()
        };

        persistentState.State.SuggestedSkills[key] = suggestion;
        await persistentState.WriteStateAsync();

        logger.LogInformation(
            "Skill {SkillId} ({Title}) suggested in workspace {WorkspaceId}",
            skill.SkillId,
            skill.Title,
            persistentState.State.WorkspaceId);

        return suggestion;
    }

    public Task<IReadOnlyList<SkillSuggestion>> GetSuggestedSkillsAsync(CapabilityToken token)
    {
        Authorize(token, SkillRead);

        IReadOnlyList<SkillSuggestion> suggestions = persistentState.State.SuggestedSkills.Values
            .OrderByDescending(s => s.SuggestedAt)
            .ToList();
        return Task.FromResult(suggestions);
    }

    public async Task<SkillDocument?> AcceptSuggestedSkillAsync(SkillId skillId, CapabilityToken token)
    {
        Authorize(token, SkillWrite);

        var key = skillId.ToString();
        if (!persistentState.State.SuggestedSkills.Remove(key, out var suggestion))
            return null;

        persistentState.State.Skills[key] = suggestion.Skill;
        await persistentState.WriteStateAsync();
        await PublishSkillCreatedAsync(suggestion.Skill, key);

        logger.LogInformation(
            "Skill suggestion {SkillId} accepted in workspace {WorkspaceId}",
            skillId,
            persistentState.State.WorkspaceId);

        return suggestion.Skill;
    }

    public async Task<bool> RejectSuggestedSkillAsync(SkillId skillId, CapabilityToken token)
    {
        Authorize(token, SkillWrite);

        var key = skillId.ToString();
        if (!persistentState.State.SuggestedSkills.Remove(key))
            return false;

        await persistentState.WriteStateAsync();
        logger.LogInformation(
            "Skill suggestion {SkillId} rejected in workspace {WorkspaceId}",
            skillId,
            persistentState.State.WorkspaceId);
        return true;
    }

    public Task<IReadOnlyList<SkillSearchResult>> SearchAsync(string query, CapabilityToken token, int maxResults = 5, SkillSearchOptions? options = null)
    {
        Authorize(token, SkillRead);

        if (persistentState.State.Skills.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<SkillSearchResult>>(new List<SkillSearchResult>());
        }

        var results = SearchSkills(
            persistentState.State.Skills.Values,
            query,
            maxResults,
            options,
            timeProvider.GetUtcNow());

        return Task.FromResult<IReadOnlyList<SkillSearchResult>>(results);
    }

    public Task<SkillDocument?> GetSkillAsync(SkillId skillId, CapabilityToken token)
    {
        Authorize(token, SkillRead);

        persistentState.State.Skills.TryGetValue(skillId.ToString(), out var skill);
        return Task.FromResult(skill);
    }

    public async Task RecordUsageAsync(SkillId skillId, bool success, CapabilityToken token)
    {
        Authorize(token, SkillWrite);

        var key = skillId.ToString();
        if (!persistentState.State.Skills.TryGetValue(key, out var skill))
            return;

        skill.UseCount++;
        skill.LastUsedAt = timeProvider.GetUtcNow();
        skill.SuccessRate = (skill.SuccessRate * (skill.UseCount - 1) + (success ? 1.0 : 0.0)) / skill.UseCount;

        await persistentState.WriteStateAsync();
    }

    public Task<IReadOnlyList<SkillDocument>> GetAllSkillsAsync(CapabilityToken token)
    {
        Authorize(token, SkillRead);

        IReadOnlyList<SkillDocument> skills = persistentState.State.Skills.Values
            .Where(skill => skill.ArchivedAt is null)
            .ToList();
        return Task.FromResult(skills);
    }

    public async Task<SkillDocument?> ArchiveSkillAsync(SkillId skillId, CapabilityToken token)
    {
        Authorize(token, SkillWrite);

        var key = skillId.ToString();
        if (!persistentState.State.Skills.TryGetValue(key, out var skill))
            return null;

        skill.ArchivedAt ??= timeProvider.GetUtcNow();
        await persistentState.WriteStateAsync();
        logger.LogInformation("Skill {SkillId} archived in workspace {WorkspaceId}", skillId, persistentState.State.WorkspaceId);
        return skill;
    }

    public async Task<SkillDocument?> RestoreSkillAsync(SkillId skillId, CapabilityToken token)
    {
        Authorize(token, SkillWrite);

        var key = skillId.ToString();
        if (!persistentState.State.Skills.TryGetValue(key, out var skill))
            return null;

        skill.ArchivedAt = null;
        await persistentState.WriteStateAsync();
        logger.LogInformation("Skill {SkillId} restored in workspace {WorkspaceId}", skillId, persistentState.State.WorkspaceId);
        return skill;
    }

    public async Task RemoveSkillAsync(SkillId skillId, CapabilityToken token)
    {
        Authorize(token, SkillWrite);

        var key = skillId.ToString();
        if (persistentState.State.Skills.Remove(key))
        {
            await persistentState.WriteStateAsync();
            logger.LogInformation("Skill {SkillId} removed from workspace {WorkspaceId}",
                skillId, persistentState.State.WorkspaceId);
        }
    }

    private void Authorize(CapabilityToken token, string grant)
    {
        if (!tokenService.Validate(token))
            throw new UnauthorizedAccessException("Invalid or expired capability token");

        if (!token.HasGrant(grant))
            throw new UnauthorizedAccessException($"Token does not grant '{grant}'");
    }

    private Task PublishSkillCreatedAsync(SkillDocument skill, string key) =>
        eventBus.PublishAsync(new SkillCreatedEvent
        {
            SourceId = key,
            WorkspaceId = WorkspaceId.From(persistentState.State.WorkspaceId),
            SkillId = skill.SkillId,
            Title = skill.Title,
            CreatedByAgent = skill.CreatedByAgent
        }, CancellationToken.None);

    private static List<SkillSearchResult> SearchSkills(
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
