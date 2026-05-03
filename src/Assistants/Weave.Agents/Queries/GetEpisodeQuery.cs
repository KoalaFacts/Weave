using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Queries;

public sealed record GetEpisodeQuery(WorkspaceId WorkspaceId, EpisodeId EpisodeId);

public sealed class GetEpisodeHandler(IVirtualActorProvider actors)
    : IQueryHandler<GetEpisodeQuery, Episode>
{
    public async Task<Episode> HandleAsync(GetEpisodeQuery query, CancellationToken ct)
    {
        var actor = actors.GetActor<IEpisodicMemoryActor>(VirtualActorId.From(query.WorkspaceId.ToString()));
        return await actor.GetEpisodeAsync(query.EpisodeId)
            ?? throw new KeyNotFoundException($"Episode '{query.EpisodeId}' not found.");
    }
}
