using Weave.Agents.Models;
using Weave.Shared.Ids;

namespace Weave.Agents.Grains;

public interface ISkillMemoryGrain : IGrainWithStringKey
{
    Task<SkillDocument> StoreSkillAsync(SkillDocument skill);
    Task<IReadOnlyList<SkillSearchResult>> SearchAsync(string query, int maxResults = 5);
    Task<SkillDocument?> GetSkillAsync(SkillId skillId);
    Task RecordUsageAsync(SkillId skillId, bool success);
    Task<IReadOnlyList<SkillDocument>> GetAllSkillsAsync();
    Task RemoveSkillAsync(SkillId skillId);
}
