using Weave.Agents.Channels;
using Weave.Agents.Heartbeat;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Models;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Agents.Lifecycle;

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
