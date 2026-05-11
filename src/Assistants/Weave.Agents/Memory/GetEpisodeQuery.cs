using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Memory;

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
