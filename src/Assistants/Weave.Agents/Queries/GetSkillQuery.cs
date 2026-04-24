using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;
using Weave.Shared.VirtualActors;

namespace Weave.Agents.Queries;

public sealed record GetSkillQuery(WorkspaceId WorkspaceId, SkillId SkillId);

public sealed class GetSkillHandler(IVirtualActorProvider actors)
    : IQueryHandler<GetSkillQuery, SkillDocument>
{
    public async Task<SkillDocument> HandleAsync(GetSkillQuery query, CancellationToken ct)
    {
        var actor = actors.GetActor<ISkillMemoryActor>(VirtualActorId.From(query.WorkspaceId.ToString()));
        return await actor.GetSkillAsync(query.SkillId)
            ?? throw new KeyNotFoundException($"Skill '{query.SkillId}' not found.");
    }
}
