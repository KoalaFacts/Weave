using Weave.Agents.Actors;
using Weave.Agents.Heartbeat;
using Weave.Shared.Cqrs;
using Weave.Shared.VirtualActors;
using Weave.Workspaces.Actors;
using Weave.Workspaces.Commands;
using Weave.Workspaces.Models;

namespace Weave.Silo.Api;

public sealed class StartWorkspaceHandler(IVirtualActorProvider actors)
    : ICommandHandler<StartWorkspaceCommand, WorkspaceState>
{
    public async Task<WorkspaceState> HandleAsync(StartWorkspaceCommand command, CancellationToken ct)
    {
        var workspaceId = command.WorkspaceId.ToString();
        var workspace = actors.GetActor<IWorkspaceActor>(VirtualActorId.From(command.WorkspaceId.ToString()));
        var state = await workspace.StartAsync(command.Manifest);
        var registry = actors.GetActor<IWorkspaceRegistryActor>(VirtualActorId.From("active"));
        await registry.RegisterAsync(workspaceId);

        var toolRegistry = actors.GetActor<IToolRegistryActor>(VirtualActorId.From(command.WorkspaceId.ToString()));
        await toolRegistry.ConnectToolsAsync(command.Manifest.Tools);
        await toolRegistry.ConfigureAccessAsync(command.Manifest.Agents.ToDictionary(
            static kvp => kvp.Key,
            static kvp => kvp.Value.Tools.ToList(),
            StringComparer.Ordinal));

        var supervisor = actors.GetActor<IAgentSupervisorActor>(VirtualActorId.From(command.WorkspaceId.ToString()));
        await supervisor.ActivateAllAsync(command.Manifest);

        foreach (var (agentName, definition) in command.Manifest.Agents)
        {
            if (definition.Heartbeat is null)
                continue;

            var heartbeat = actors.GetActor<IHeartbeatActor>(VirtualActorId.Combine(command.WorkspaceId, agentName));
            await heartbeat.StartAsync(new Weave.Agents.Heartbeat.HeartbeatConfig
            {
                Cron = definition.Heartbeat.Cron,
                Tasks = definition.Heartbeat.Tasks,
                Enabled = true
            });
        }

        return await workspace.GetStateAsync();
    }
}

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
