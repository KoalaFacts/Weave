using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Queries;

public sealed record GetChannelsQuery(WorkspaceId WorkspaceId);

public sealed class GetChannelsHandler(IGrainFactory grainFactory)
    : IQueryHandler<GetChannelsQuery, IReadOnlyList<ChannelConfig>>
{
    public async Task<IReadOnlyList<ChannelConfig>> HandleAsync(GetChannelsQuery query, CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<IChannelGatewayGrain>(query.WorkspaceId.ToString());
        return await grain.GetChannelsAsync();
    }
}
