using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Queries;

public sealed record GetAgentStateQuery(WorkspaceId WorkspaceId, string AgentName);

public sealed class GetAgentStateHandler(IGrainFactory grainFactory)
    : IQueryHandler<GetAgentStateQuery, AgentState?>
{
    public async Task<AgentState?> HandleAsync(GetAgentStateQuery query, CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<IAgentGrain>($"{query.WorkspaceId}/{query.AgentName}");
        var state = await grain.GetStateAsync();
        return string.IsNullOrWhiteSpace(state.AgentId) ? null : state;
    }
}
