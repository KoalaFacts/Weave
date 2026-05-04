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
