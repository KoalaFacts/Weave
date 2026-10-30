using Weave.Agents.Channels;
using Weave.Agents.Chat;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Lifecycle;

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
