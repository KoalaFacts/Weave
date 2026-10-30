using Microsoft.Extensions.AI;
using Weave.Workspaces.Manifest;

namespace Weave.Agents.Pipeline;

public interface IAgentChatClientFactory
{
    Task<IChatClient> CreateAsync(string agentId, AgentDefinition? definition, CancellationToken ct = default);
}
