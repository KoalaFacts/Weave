using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Memory;

public sealed record StoreEpisodeCommand(WorkspaceId WorkspaceId, Episode Episode);

public sealed class StoreEpisodeHandler(IVirtualActorProvider actors)
    : ICommandHandler<StoreEpisodeCommand, Episode>
{
    public async Task<Episode> HandleAsync(StoreEpisodeCommand command, CancellationToken ct)
    {
        var actor = actors.GetActor<IEpisodicMemoryActor>(VirtualActorId.From(command.WorkspaceId.ToString()));
        return await actor.StoreEpisodeAsync(command.Episode);
    }
}
