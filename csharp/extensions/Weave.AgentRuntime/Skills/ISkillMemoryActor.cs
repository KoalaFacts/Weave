using Weave.Security.Tokens;
using Weave.Shared.Ids;

namespace Weave.Agents.Skills;

public interface ISkillMemoryActor
{
    Task<SkillDocument> StoreSkillAsync(SkillDocument skill, CapabilityToken token);
    Task<SkillSuggestion> SuggestSkillAsync(SkillDocument skill, CapabilityToken token, string? sourceTaskId = null);
    Task<IReadOnlyList<SkillSuggestion>> GetSuggestedSkillsAsync(CapabilityToken token);
    Task<SkillDocument?> AcceptSuggestedSkillAsync(SkillId skillId, CapabilityToken token);
    Task<bool> RejectSuggestedSkillAsync(SkillId skillId, CapabilityToken token);
    Task<IReadOnlyList<SkillSearchResult>> SearchAsync(string query, CapabilityToken token, int maxResults = 5, SkillSearchOptions? options = null);
    Task<SkillDocument?> GetSkillAsync(SkillId skillId, CapabilityToken token);
    Task RecordUsageAsync(SkillId skillId, bool success, CapabilityToken token);
    Task<IReadOnlyList<SkillDocument>> GetAllSkillsAsync(CapabilityToken token);
    Task<SkillDocument?> ArchiveSkillAsync(SkillId skillId, CapabilityToken token);
    Task<SkillDocument?> RestoreSkillAsync(SkillId skillId, CapabilityToken token);
    Task RemoveSkillAsync(SkillId skillId, CapabilityToken token);
}
