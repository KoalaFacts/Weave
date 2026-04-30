using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Queries;

public sealed record GetChannelsQuery(WorkspaceId WorkspaceId);

public sealed class GetChannelsHandler(IVirtualActorProvider actors)
    : IQueryHandler<GetChannelsQuery, IReadOnlyList<ChannelConfig>>
{
    public async Task<IReadOnlyList<ChannelConfig>> HandleAsync(GetChannelsQuery query, CancellationToken ct)
    {
        var actor = actors.GetActor<IChannelGatewayActor>(VirtualActorId.From(query.WorkspaceId.ToString()));
        return await actor.GetChannelsAsync();
    }
}
