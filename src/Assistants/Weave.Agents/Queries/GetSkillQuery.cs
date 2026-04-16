using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Queries;

public sealed record GetSkillQuery(WorkspaceId WorkspaceId, SkillId SkillId);

public sealed class GetSkillHandler(IGrainFactory grainFactory)
    : IQueryHandler<GetSkillQuery, SkillDocument>
{
    public async Task<SkillDocument> HandleAsync(GetSkillQuery query, CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<ISkillMemoryGrain>(query.WorkspaceId.ToString());
        return await grain.GetSkillAsync(query.SkillId)
            ?? throw new KeyNotFoundException($"Skill '{query.SkillId}' not found.");
    }
}
