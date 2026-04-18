using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Queries;

public sealed record SearchSkillsQuery(WorkspaceId WorkspaceId, string Query, int MaxResults = 5);

public sealed class SearchSkillsHandler(IGrainFactory grainFactory)
    : IQueryHandler<SearchSkillsQuery, IReadOnlyList<SkillSearchResult>>
{
    public async Task<IReadOnlyList<SkillSearchResult>> HandleAsync(SearchSkillsQuery query, CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<ISkillMemoryGrain>(query.WorkspaceId.ToString());
        return await grain.SearchAsync(query.Query, query.MaxResults);
    }
}
