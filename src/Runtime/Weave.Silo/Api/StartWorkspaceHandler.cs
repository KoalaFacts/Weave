using Weave.Agents.Channels;
using Weave.Agents.Heartbeat;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Shared.Cqrs;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Models;
using Weave.Workspaces.Registry;
using Weave.Workspaces.Templates;

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
