using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Commands;

public sealed record SendAgentMessageCommand(WorkspaceId WorkspaceId, string AgentName, AgentMessage Message);

public sealed class SendAgentMessageHandler(IGrainFactory grainFactory)
    : ICommandHandler<SendAgentMessageCommand, AgentChatResponse>
{
    public async Task<AgentChatResponse> HandleAsync(SendAgentMessageCommand command, CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<IAgentGrain>($"{command.WorkspaceId}/{command.AgentName}");
        return await grain.SendAsync(command.Message);
    }
}
