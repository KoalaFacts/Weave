using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Commands;

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
