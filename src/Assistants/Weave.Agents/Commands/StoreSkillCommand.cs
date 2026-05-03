using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Security.Tokens;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Commands;

public sealed record StoreSkillCommand(WorkspaceId WorkspaceId, SkillDocument Skill, CapabilityToken Token);

public sealed class StoreSkillHandler(IVirtualActorProvider actors)
    : ICommandHandler<StoreSkillCommand, SkillDocument>
{
    public async Task<SkillDocument> HandleAsync(StoreSkillCommand command, CancellationToken ct)
    {
        var actor = actors.GetActor<ISkillMemoryActor>(VirtualActorId.From(command.WorkspaceId.ToString()));
        return await actor.StoreSkillAsync(command.Skill, command.Token);
    }
}
