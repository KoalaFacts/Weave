namespace Weave.Agents.Pipeline;

public sealed class AgentCostSummary
{
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public int RequestCount { get; set; }
    public string LastModel { get; set; } = string.Empty;
}
