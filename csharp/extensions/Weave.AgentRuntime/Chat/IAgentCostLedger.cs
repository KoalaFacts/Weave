namespace Weave.Agents.Pipeline;

public interface IAgentCostLedger
{
    void RecordUsage(string agentId, string modelId, long inputTokens, long outputTokens);
    AgentCostSummary? GetCostSummary(string agentId);
    IReadOnlyDictionary<string, AgentCostSummary> GetAllCosts();
}
