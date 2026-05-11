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
