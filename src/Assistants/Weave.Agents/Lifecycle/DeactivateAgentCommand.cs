using Weave.Agents.Channels;
using Weave.Agents.Heartbeat;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Lifecycle;

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
