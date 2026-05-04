using Weave.Agents.Lifecycle;
using Weave.Agents.Channels;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Models;
using Weave.Security.Tokens;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Skills;

public sealed record SearchSkillsQuery(
    WorkspaceId WorkspaceId,
    string Query,
    CapabilityToken Token,
    int MaxResults = 5,
    SkillSearchOptions? Options = null);

public sealed class SearchSkillsHandler(IVirtualActorProvider actors)
    : IQueryHandler<SearchSkillsQuery, IReadOnlyList<SkillSearchResult>>
{
    public async Task<IReadOnlyList<SkillSearchResult>> HandleAsync(SearchSkillsQuery query, CancellationToken ct)
    {
        var actor = actors.GetActor<ISkillMemoryActor>(VirtualActorId.From(query.WorkspaceId.ToString()));
        return await actor.SearchAsync(query.Query, query.Token, query.MaxResults, query.Options);
    }
}
