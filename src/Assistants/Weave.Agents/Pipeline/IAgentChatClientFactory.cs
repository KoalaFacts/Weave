using Microsoft.Extensions.AI;

namespace Weave.Agents.Pipeline;

public interface IAgentChatClientFactory
{
    IChatClient Create(string agentId, string? modelId = null);
}
