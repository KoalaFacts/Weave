using Orleans;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;

namespace Weave.Agents.Queries;

public sealed record GetAgentStateQuery(string WorkspaceId, string AgentName);

public sealed class GetAgentStateHandler(IGrainFactory grainFactory)
    : IQueryHandler<GetAgentStateQuery, AgentState>
{
    public async Task<AgentState> HandleAsync(GetAgentStateQuery query, CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<IAgentGrain>($"{query.WorkspaceId}/{query.AgentName}");
        return await grain.GetStateAsync();
    }
}
