using Weave.Agents.Chat;
using Weave.Agents.Lifecycle;
namespace Weave.Agents.Pipeline;

public interface IAgentChatPipeline
{
    void Initialize(string agentId, string? model);
    void Reset();
    Task<AgentChatResponse> ExecuteAsync(AgentState state, AgentMessage message);
}
