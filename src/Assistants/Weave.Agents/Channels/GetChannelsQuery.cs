using Weave.Agents.Lifecycle;
using Weave.Agents.Channels;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Channels;

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
