using Weave.Agents.Actors;
using Weave.Agents.Heartbeat;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Agents.Commands;

public sealed record ActivateAgentCommand(WorkspaceId WorkspaceId, string AgentName, AgentDefinition Definition);

public sealed class ActivateAgentHandler(IVirtualActorProvider actors)
    : ICommandHandler<ActivateAgentCommand, AgentState>
{
    public async Task<AgentState> HandleAsync(ActivateAgentCommand command, CancellationToken ct)
    {
        var toolNames = command.Definition.Tools ?? [];
        var registry = actors.GetActor<IToolRegistryActor>(VirtualActorId.From(command.WorkspaceId.ToString()));
        await registry.GrantAgentToolsAsync(command.AgentName, toolNames);

        var actor = actors.GetActor<IAgentActor>(VirtualActorId.Combine(command.WorkspaceId, command.AgentName));
        var state = await actor.ActivateAgentAsync(command.WorkspaceId, command.Definition);

        foreach (var toolName in toolNames)
            await actor.ConnectToolAsync(toolName);

        if (command.Definition.Heartbeat is not null)
        {
            var heartbeat = actors.GetActor<IHeartbeatActor>(VirtualActorId.Combine(command.WorkspaceId, command.AgentName));
            await heartbeat.StartAsync(new Heartbeat.HeartbeatConfig
            {
                Cron = command.Definition.Heartbeat.Cron,
                Tasks = command.Definition.Heartbeat.Tasks,
                Enabled = true
            });
        }

        return state;
    }
}
