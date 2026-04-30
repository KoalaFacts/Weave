using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Silo.VirtualActors;

public sealed class SkillMemoryActorGrain : Grain, ISkillMemoryActorGrain
{
    private readonly SkillMemoryActor _actor;

    public SkillMemoryActorGrain(
        IEventBus eventBus,
        TimeProvider timeProvider,
        ILogger<SkillMemoryActor> logger,
        [PersistentState("skill-memory", "Default")] IPersistentState<SkillMemoryState> state)
    {
        _actor = new SkillMemoryActor(eventBus, timeProvider, logger,
            new OrleansActorState<SkillMemoryState>(state));
    }

    public Task<SkillDocument> StoreSkillAsync(SkillDocument skill) => _actor.StoreSkillAsync(skill);
    public Task<SkillSuggestion> SuggestSkillAsync(SkillDocument skill, string? sourceTaskId = null) =>
        _actor.SuggestSkillAsync(skill, sourceTaskId);
    public Task<IReadOnlyList<SkillSuggestion>> GetSuggestedSkillsAsync() => _actor.GetSuggestedSkillsAsync();
    public Task<SkillDocument?> AcceptSuggestedSkillAsync(SkillId skillId) => _actor.AcceptSuggestedSkillAsync(skillId);
    public Task<bool> RejectSuggestedSkillAsync(SkillId skillId) => _actor.RejectSuggestedSkillAsync(skillId);
    public Task<IReadOnlyList<SkillSearchResult>> SearchAsync(string query, int maxResults = 5, SkillSearchOptions? options = null) =>
        _actor.SearchAsync(query, maxResults, options);
    public Task<SkillDocument?> GetSkillAsync(SkillId skillId) => _actor.GetSkillAsync(skillId);
    public Task RecordUsageAsync(SkillId skillId, bool success) => _actor.RecordUsageAsync(skillId, success);
    public Task<IReadOnlyList<SkillDocument>> GetAllSkillsAsync() => _actor.GetAllSkillsAsync();
    public Task<SkillDocument?> ArchiveSkillAsync(SkillId skillId) => _actor.ArchiveSkillAsync(skillId);
    public Task<SkillDocument?> RestoreSkillAsync(SkillId skillId) => _actor.RestoreSkillAsync(skillId);
    public Task RemoveSkillAsync(SkillId skillId) => _actor.RemoveSkillAsync(skillId);
}
