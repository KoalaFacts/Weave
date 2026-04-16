using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Commands;

public sealed record StoreSkillCommand(WorkspaceId WorkspaceId, SkillDocument Skill);

public sealed class StoreSkillHandler(IGrainFactory grainFactory)
    : ICommandHandler<StoreSkillCommand, SkillDocument>
{
    public async Task<SkillDocument> HandleAsync(StoreSkillCommand command, CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<ISkillMemoryGrain>(command.WorkspaceId.ToString());
        return await grain.StoreSkillAsync(command.Skill);
    }
}
