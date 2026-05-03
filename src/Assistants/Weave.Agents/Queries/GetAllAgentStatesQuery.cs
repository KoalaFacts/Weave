using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Queries;

public sealed record GetAllAgentStatesQuery(WorkspaceId WorkspaceId);

public sealed class GetAllAgentStatesHandler(IVirtualActorProvider actors)
    : IQueryHandler<GetAllAgentStatesQuery, IReadOnlyList<AgentState>>
{
    public async Task<IReadOnlyList<AgentState>> HandleAsync(GetAllAgentStatesQuery query, CancellationToken ct)
    {
        var supervisor = actors.GetActor<IAgentSupervisorActor>(VirtualActorId.From(query.WorkspaceId.ToString()));
        return await supervisor.GetAllAgentStatesAsync();
    }
}
