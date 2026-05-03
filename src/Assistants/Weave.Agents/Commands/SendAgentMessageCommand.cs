using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Commands;

public sealed record SendAgentMessageCommand(WorkspaceId WorkspaceId, string AgentName, AgentMessage Message);

public sealed class SendAgentMessageHandler(IVirtualActorProvider actors)
    : ICommandHandler<SendAgentMessageCommand, AgentChatResponse>
{
    public async Task<AgentChatResponse> HandleAsync(SendAgentMessageCommand command, CancellationToken ct)
    {
        var actor = actors.GetActor<IAgentActor>(VirtualActorId.Combine(command.WorkspaceId, command.AgentName));
        return await actor.SendAsync(command.Message);
    }
}
