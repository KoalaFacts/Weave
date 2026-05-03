using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Queries;

public sealed record RecallEpisodesQuery(
    WorkspaceId WorkspaceId,
    string Query,
    int MaxResults = 3,
    EpisodeSearchOptions? Options = null);

public sealed class RecallEpisodesHandler(IVirtualActorProvider actors)
    : IQueryHandler<RecallEpisodesQuery, IReadOnlyList<EpisodeSearchResult>>
{
    public async Task<IReadOnlyList<EpisodeSearchResult>> HandleAsync(RecallEpisodesQuery query, CancellationToken ct)
    {
        var actor = actors.GetActor<IEpisodicMemoryActor>(VirtualActorId.From(query.WorkspaceId.ToString()));
        return await actor.RecallAsync(query.Query, query.MaxResults, query.Options);
    }
}
