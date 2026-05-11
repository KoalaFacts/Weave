using Weave.Agents.Chat;
using Weave.Agents.Lifecycle;
using Weave.Workspaces.Manifest;
namespace Weave.Agents.Pipeline;

public interface IAgentChatPipeline
{
    Task InitializeAsync(string agentId, AgentDefinition? definition, CancellationToken ct = default);
    void Reset();
    Task<AgentChatResponse> ExecuteAsync(AgentState state, AgentMessage message);
    IAsyncEnumerable<AgentChatStreamingFrame> ExecuteStreamingAsync(AgentState state, AgentMessage message, CancellationToken ct = default);
}
