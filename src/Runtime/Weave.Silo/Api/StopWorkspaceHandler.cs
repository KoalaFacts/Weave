using Weave.Agents.Actors;
using Weave.Agents.Heartbeat;
using Weave.Shared.Cqrs;
using Weave.Workspaces.Actors;
using Weave.Workspaces.Commands;

namespace Weave.Silo.Api;

public sealed class StopWorkspaceHandler(IVirtualActorProvider actors)
    : ICommandHandler<StopWorkspaceCommand, bool>
{
    public async Task<bool> HandleAsync(StopWorkspaceCommand command, CancellationToken ct)
    {
        var workspaceId = command.WorkspaceId.ToString();
        var workspace = actors.GetActor<IWorkspaceActor>(VirtualActorId.From(command.WorkspaceId.ToString()));
        var state = await workspace.GetStateAsync();

        foreach (var agentName in state.ActiveAgents)
        {
            var heartbeat = actors.GetActor<IHeartbeatActor>(VirtualActorId.Combine(command.WorkspaceId, agentName));
            await heartbeat.StopAsync();
        }

        var supervisor = actors.GetActor<IAgentSupervisorActor>(VirtualActorId.From(command.WorkspaceId.ToString()));
        await supervisor.DeactivateAllAsync();

        var toolRegistry = actors.GetActor<IToolRegistryActor>(VirtualActorId.From(command.WorkspaceId.ToString()));
        await toolRegistry.DisconnectAllAsync();

        await workspace.StopAsync();

        var registry = actors.GetActor<IWorkspaceRegistryActor>(VirtualActorId.From("active"));
        await registry.UnregisterAsync(workspaceId);

        return true;
    }
}
