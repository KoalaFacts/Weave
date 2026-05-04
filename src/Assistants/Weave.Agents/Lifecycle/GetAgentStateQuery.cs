using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Models;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Lifecycle;

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
