using System.Collections.Concurrent;

namespace Weave.Agents.Pipeline;

public interface IAgentCostLedger
{
    void RecordUsage(string agentId, string modelId, long inputTokens, long outputTokens);
    AgentCostSummary? GetCostSummary(string agentId);
    IReadOnlyDictionary<string, AgentCostSummary> GetAllCosts();
}

public sealed class AgentCostLedger : IAgentCostLedger
{
    private readonly ConcurrentDictionary<string, AgentCostSummary> _costs = new();

    public void RecordUsage(string agentId, string modelId, long inputTokens, long outputTokens)
    {
        var summary = _costs.GetOrAdd(agentId, static _ => new AgentCostSummary());
        summary.TotalInputTokens += inputTokens;
        summary.TotalOutputTokens += outputTokens;
        summary.RequestCount++;
        summary.LastModel = modelId;
    }

    public AgentCostSummary? GetCostSummary(string agentId) =>
        _costs.TryGetValue(agentId, out var summary) ? summary : null;

    public IReadOnlyDictionary<string, AgentCostSummary> GetAllCosts() => _costs;
}
