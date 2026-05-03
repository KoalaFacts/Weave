using Weave.Agents.Actors;
using Weave.Agents.Heartbeat;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Commands;

public sealed record DeactivateAgentCommand(WorkspaceId WorkspaceId, string AgentName);

public sealed class DeactivateAgentHandler(IVirtualActorProvider actors)
    : ICommandHandler<DeactivateAgentCommand, bool>
{
    public async Task<bool> HandleAsync(DeactivateAgentCommand command, CancellationToken ct)
    {
        var heartbeat = actors.GetActor<IHeartbeatActor>(VirtualActorId.Combine(command.WorkspaceId, command.AgentName));
        await heartbeat.StopAsync();

        var actor = actors.GetActor<IAgentActor>(VirtualActorId.Combine(command.WorkspaceId, command.AgentName));
        await actor.DeactivateAsync();
        return true;
    }
}
