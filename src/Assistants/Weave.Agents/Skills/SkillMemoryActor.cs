using Microsoft.Extensions.Logging;
using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Models;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Skills;

public sealed class SkillMemoryActor(
    IEventBus eventBus,
    TimeProvider timeProvider,
    ICapabilityAuthorizer authorizer,
    ILogger<SkillMemoryActor> logger,
    IActorState<SkillMemoryState> persistentState) : ISkillMemoryActor
{
    private const string SkillRead = "skill:read";
    private const string SkillWrite = "skill:write";

    public async Task OnActivatedAsync(string? key, CancellationToken cancellationToken)
    {
        await persistentState.ReadStateAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(persistentState.State.WorkspaceId) && !string.IsNullOrWhiteSpace(key))
        {
            persistentState.State.WorkspaceId = key;
            await persistentState.WriteStateAsync(cancellationToken);
        }
    }

    public async Task<SkillDocument> StoreSkillAsync(SkillDocument skill, CapabilityToken token)
    {
        await authorizer.AuthorizeAsync(token, SkillWrite, persistentState.State.WorkspaceId);

        var key = skill.SkillId.ToString();
        persistentState.State.Skills[key] = skill;
        await persistentState.WriteStateAsync(token.CancellationToken);

        await PublishSkillCreatedAsync(skill, key, token.CancellationToken);

        logger.LogInformation("Skill {SkillId} ({Title}) stored in workspace {WorkspaceId}",
            skill.SkillId, skill.Title, persistentState.State.WorkspaceId);

        return skill;
    }

    public async Task<SkillSuggestion> SuggestSkillAsync(SkillDocument skill, CapabilityToken token, string? sourceTaskId = null)
    {
        await authorizer.AuthorizeAsync(token, SkillWrite, persistentState.State.WorkspaceId);

        var key = skill.SkillId.ToString();
        var suggestion = new SkillSuggestion
        {
            Skill = skill,
            SourceTaskId = sourceTaskId,
            SuggestedAt = timeProvider.GetUtcNow()
        };

        persistentState.State.SuggestedSkills[key] = suggestion;
        await persistentState.WriteStateAsync(token.CancellationToken);

        logger.LogInformation(
            "Skill {SkillId} ({Title}) suggested in workspace {WorkspaceId}",
            skill.SkillId,
            skill.Title,
            persistentState.State.WorkspaceId);

        return suggestion;
    }

    public async Task<IReadOnlyList<SkillSuggestion>> GetSuggestedSkillsAsync(CapabilityToken token)
    {
        await authorizer.AuthorizeAsync(token, SkillRead, persistentState.State.WorkspaceId);

        IReadOnlyList<SkillSuggestion> suggestions = persistentState.State.SuggestedSkills.Values
            .OrderByDescending(s => s.SuggestedAt)
            .ToList();
        return suggestions;
    }

    public async Task<SkillDocument?> AcceptSuggestedSkillAsync(SkillId skillId, CapabilityToken token)
    {
        await authorizer.AuthorizeAsync(token, SkillWrite, persistentState.State.WorkspaceId);

        var key = skillId.ToString();
        if (!persistentState.State.SuggestedSkills.Remove(key, out var suggestion))
            return null;

        persistentState.State.Skills[key] = suggestion.Skill;
        await persistentState.WriteStateAsync(token.CancellationToken);
        await PublishSkillCreatedAsync(suggestion.Skill, key, token.CancellationToken);

        logger.LogInformation(
            "Skill suggestion {SkillId} accepted in workspace {WorkspaceId}",
            skillId,
            persistentState.State.WorkspaceId);

        return suggestion.Skill;
    }

    public async Task<bool> RejectSuggestedSkillAsync(SkillId skillId, CapabilityToken token)
    {
        await authorizer.AuthorizeAsync(token, SkillWrite, persistentState.State.WorkspaceId);

        var key = skillId.ToString();
        if (!persistentState.State.SuggestedSkills.Remove(key))
            return false;

        await persistentState.WriteStateAsync(token.CancellationToken);
        logger.LogInformation(
            "Skill suggestion {SkillId} rejected in workspace {WorkspaceId}",
            skillId,
            persistentState.State.WorkspaceId);
        return true;
    }

    public async Task<IReadOnlyList<SkillSearchResult>> SearchAsync(string query, CapabilityToken token, int maxResults = 5, SkillSearchOptions? options = null)
    {
        await authorizer.AuthorizeAsync(token, SkillRead, persistentState.State.WorkspaceId);

        if (persistentState.State.Skills.Count == 0)
        {
            return new List<SkillSearchResult>();
        }

        return SearchSkills(
            persistentState.State.Skills.Values,
            query,
            maxResults,
            options,
            timeProvider.GetUtcNow());
    }

    public async Task<SkillDocument?> GetSkillAsync(SkillId skillId, CapabilityToken token)
    {
        await authorizer.AuthorizeAsync(token, SkillRead, persistentState.State.WorkspaceId);

        persistentState.State.Skills.TryGetValue(skillId.ToString(), out var skill);
        return skill;
    }

    public async Task RecordUsageAsync(SkillId skillId, bool success, CapabilityToken token)
    {
        await authorizer.AuthorizeAsync(token, SkillWrite, persistentState.State.WorkspaceId);

        var key = skillId.ToString();
        if (!persistentState.State.Skills.TryGetValue(key, out var skill))
            return;

        skill.UseCount++;
        skill.LastUsedAt = timeProvider.GetUtcNow();
        skill.SuccessRate = (skill.SuccessRate * (skill.UseCount - 1) + (success ? 1.0 : 0.0)) / skill.UseCount;

        await persistentState.WriteStateAsync(token.CancellationToken);
    }

    public async Task<IReadOnlyList<SkillDocument>> GetAllSkillsAsync(CapabilityToken token)
    {
        await authorizer.AuthorizeAsync(token, SkillRead, persistentState.State.WorkspaceId);

        IReadOnlyList<SkillDocument> skills = persistentState.State.Skills.Values
            .Where(skill => skill.ArchivedAt is null)
            .ToList();
        return skills;
    }

    public async Task<SkillDocument?> ArchiveSkillAsync(SkillId skillId, CapabilityToken token)
    {
        await authorizer.AuthorizeAsync(token, SkillWrite, persistentState.State.WorkspaceId);

        var key = skillId.ToString();
        if (!persistentState.State.Skills.TryGetValue(key, out var skill))
            return null;

        skill.ArchivedAt ??= timeProvider.GetUtcNow();
        await persistentState.WriteStateAsync(token.CancellationToken);
        logger.LogInformation("Skill {SkillId} archived in workspace {WorkspaceId}", skillId, persistentState.State.WorkspaceId);
        return skill;
    }

    public async Task<SkillDocument?> RestoreSkillAsync(SkillId skillId, CapabilityToken token)
    {
        await authorizer.AuthorizeAsync(token, SkillWrite, persistentState.State.WorkspaceId);

        var key = skillId.ToString();
        if (!persistentState.State.Skills.TryGetValue(key, out var skill))
            return null;

        skill.ArchivedAt = null;
        await persistentState.WriteStateAsync(token.CancellationToken);
        logger.LogInformation("Skill {SkillId} restored in workspace {WorkspaceId}", skillId, persistentState.State.WorkspaceId);
        return skill;
    }

    public async Task RemoveSkillAsync(SkillId skillId, CapabilityToken token)
    {
        await authorizer.AuthorizeAsync(token, SkillWrite, persistentState.State.WorkspaceId);

        var key = skillId.ToString();
        if (persistentState.State.Skills.Remove(key))
        {
            await persistentState.WriteStateAsync(token.CancellationToken);
            logger.LogInformation("Skill {SkillId} removed from workspace {WorkspaceId}",
                skillId, persistentState.State.WorkspaceId);
        }
    }

    private Task PublishSkillCreatedAsync(SkillDocument skill, string key, CancellationToken cancellationToken) =>
        eventBus.PublishAsync(new SkillCreatedEvent
        {
            SourceId = key,
            WorkspaceId = WorkspaceId.From(persistentState.State.WorkspaceId),
            SkillId = skill.SkillId,
            Title = skill.Title,
            CreatedByAgent = skill.CreatedByAgent
        }, cancellationToken);

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
