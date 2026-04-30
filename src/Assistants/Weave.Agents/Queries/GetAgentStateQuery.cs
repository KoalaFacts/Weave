using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Queries;

public sealed record GetAgentStateQuery(WorkspaceId WorkspaceId, string AgentName);

public sealed class GetAgentStateHandler(IVirtualActorProvider actors)
    : IQueryHandler<GetAgentStateQuery, AgentState>
{
    public async Task<AgentState> HandleAsync(GetAgentStateQuery query, CancellationToken ct)
    {
        var actor = actors.GetActor<IAgentActor>(VirtualActorId.Combine(query.WorkspaceId, query.AgentName));
        return await actor.GetStateAsync();
    }
}
