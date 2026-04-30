using Weave.Agents.Models;
using Weave.Shared.Ids;

namespace Weave.Agents.Actors;

public interface ISkillMemoryActor
{
    Task<SkillDocument> StoreSkillAsync(SkillDocument skill);
    Task<SkillSuggestion> SuggestSkillAsync(SkillDocument skill, string? sourceTaskId = null);
    Task<IReadOnlyList<SkillSuggestion>> GetSuggestedSkillsAsync();
    Task<SkillDocument?> AcceptSuggestedSkillAsync(SkillId skillId);
    Task<bool> RejectSuggestedSkillAsync(SkillId skillId);
    Task<IReadOnlyList<SkillSearchResult>> SearchAsync(string query, int maxResults = 5, SkillSearchOptions? options = null);
    Task<SkillDocument?> GetSkillAsync(SkillId skillId);
    Task RecordUsageAsync(SkillId skillId, bool success);
    Task<IReadOnlyList<SkillDocument>> GetAllSkillsAsync();
    Task<SkillDocument?> ArchiveSkillAsync(SkillId skillId);
    Task<SkillDocument?> RestoreSkillAsync(SkillId skillId);
    Task RemoveSkillAsync(SkillId skillId);
}
