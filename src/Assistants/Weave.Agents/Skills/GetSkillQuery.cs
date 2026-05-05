using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Security.Tokens;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Skills;

public sealed record GetSkillQuery(WorkspaceId WorkspaceId, SkillId SkillId, CapabilityToken Token);

public sealed class GetSkillHandler(IVirtualActorProvider actors)
    : IQueryHandler<GetSkillQuery, SkillDocument>
{
    public async Task<SkillDocument> HandleAsync(GetSkillQuery query, CancellationToken ct)
    {
        var actor = actors.GetActor<ISkillMemoryActor>(VirtualActorId.From(query.WorkspaceId.ToString()));
        return await actor.GetSkillAsync(query.SkillId, query.Token)
            ?? throw new KeyNotFoundException($"Skill '{query.SkillId}' not found.");
    }
}
