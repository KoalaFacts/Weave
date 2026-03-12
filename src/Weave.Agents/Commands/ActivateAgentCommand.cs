using Weave.Agents.Grains;
using Weave.Agents.Heartbeat;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Agents.Commands;

public sealed record ActivateAgentCommand(WorkspaceId WorkspaceId, string AgentName, AgentDefinition Definition);

public sealed class ActivateAgentHandler(IGrainFactory grainFactory)
    : ICommandHandler<ActivateAgentCommand, AgentState>
{
    public async Task<AgentState> HandleAsync(ActivateAgentCommand command, CancellationToken ct)
    {
        var registry = grainFactory.GetGrain<IToolRegistryGrain>(command.WorkspaceId.ToString());
        await registry.GrantAgentToolsAsync(command.AgentName, command.Definition.Tools);

        var grain = grainFactory.GetGrain<IAgentGrain>($"{command.WorkspaceId}/{command.AgentName}");
        var state = await grain.ActivateAgentAsync(command.WorkspaceId, command.Definition);

        foreach (var toolName in command.Definition.Tools)
            await grain.ConnectToolAsync(toolName);

        if (command.Definition.Heartbeat is not null)
        {
            var heartbeat = grainFactory.GetGrain<IHeartbeatGrain>($"{command.WorkspaceId}/{command.AgentName}");
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
