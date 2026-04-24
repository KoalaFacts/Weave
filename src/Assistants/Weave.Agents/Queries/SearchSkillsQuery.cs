using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;
using Weave.Shared.VirtualActors;

namespace Weave.Agents.Queries;

public sealed record SearchSkillsQuery(WorkspaceId WorkspaceId, string Query, int MaxResults = 5);

public sealed class SearchSkillsHandler(IVirtualActorProvider actors)
    : IQueryHandler<SearchSkillsQuery, IReadOnlyList<SkillSearchResult>>
{
    public async Task<IReadOnlyList<SkillSearchResult>> HandleAsync(SearchSkillsQuery query, CancellationToken ct)
    {
        var actor = actors.GetActor<ISkillMemoryActor>(VirtualActorId.From(query.WorkspaceId.ToString()));
        return await actor.SearchAsync(query.Query, query.MaxResults);
    }
}
