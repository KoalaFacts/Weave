using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Silo.VirtualActors;

public sealed class SkillMemoryActorGrain : Grain, ISkillMemoryActorGrain
{
    private readonly SkillMemoryActor _actor;

    public SkillMemoryActorGrain(
        IEventBus eventBus,
        TimeProvider timeProvider,
        ICapabilityAuthorizer authorizer,
        ILogger<SkillMemoryActor> logger,
        [PersistentState("skill-memory", "Default")] IPersistentState<SkillMemoryState> state)
    {
        _actor = new SkillMemoryActor(eventBus, timeProvider, authorizer, logger,
            new OrleansActorState<SkillMemoryState>(state));
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        _actor.OnActivatedAsync(this.GetPrimaryKeyString(), cancellationToken);

    public Task<SkillDocument> StoreSkillAsync(SkillDocument skill, CapabilityToken token) =>
        _actor.StoreSkillAsync(skill, token);
    public Task<SkillSuggestion> SuggestSkillAsync(SkillDocument skill, CapabilityToken token, string? sourceTaskId = null) =>
        _actor.SuggestSkillAsync(skill, token, sourceTaskId);
    public Task<IReadOnlyList<SkillSuggestion>> GetSuggestedSkillsAsync(CapabilityToken token) =>
        _actor.GetSuggestedSkillsAsync(token);
    public Task<SkillDocument?> AcceptSuggestedSkillAsync(SkillId skillId, CapabilityToken token) =>
        _actor.AcceptSuggestedSkillAsync(skillId, token);
    public Task<bool> RejectSuggestedSkillAsync(SkillId skillId, CapabilityToken token) =>
        _actor.RejectSuggestedSkillAsync(skillId, token);
    public Task<IReadOnlyList<SkillSearchResult>> SearchAsync(string query, CapabilityToken token, int maxResults = 5, SkillSearchOptions? options = null) =>
        _actor.SearchAsync(query, token, maxResults, options);
    public Task<SkillDocument?> GetSkillAsync(SkillId skillId, CapabilityToken token) =>
        _actor.GetSkillAsync(skillId, token);
    public Task RecordUsageAsync(SkillId skillId, bool success, CapabilityToken token) =>
        _actor.RecordUsageAsync(skillId, success, token);
    public Task<IReadOnlyList<SkillDocument>> GetAllSkillsAsync(CapabilityToken token) =>
        _actor.GetAllSkillsAsync(token);
    public Task<SkillDocument?> ArchiveSkillAsync(SkillId skillId, CapabilityToken token) =>
        _actor.ArchiveSkillAsync(skillId, token);
    public Task<SkillDocument?> RestoreSkillAsync(SkillId skillId, CapabilityToken token) =>
        _actor.RestoreSkillAsync(skillId, token);
    public Task RemoveSkillAsync(SkillId skillId, CapabilityToken token) =>
        _actor.RemoveSkillAsync(skillId, token);
}
