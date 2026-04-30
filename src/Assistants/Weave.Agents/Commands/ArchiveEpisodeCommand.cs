using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Commands;

public sealed record ArchiveEpisodeCommand(WorkspaceId WorkspaceId, EpisodeId EpisodeId);

public sealed class ArchiveEpisodeHandler(IVirtualActorProvider actors)
    : ICommandHandler<ArchiveEpisodeCommand, Episode>
{
    public async Task<Episode> HandleAsync(ArchiveEpisodeCommand command, CancellationToken ct)
    {
        var actor = actors.GetActor<IEpisodicMemoryActor>(VirtualActorId.From(command.WorkspaceId.ToString()));
        return await actor.ArchiveEpisodeAsync(command.EpisodeId)
            ?? throw new KeyNotFoundException($"Episode '{command.EpisodeId}' not found.");
    }
}
