using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Weave.Agents.Pipeline;

/// <summary>
/// IChatClient middleware that tracks token usage and estimated cost per agent.
/// </summary>
public sealed partial class CostTrackingChatClient : DelegatingChatClient
{
    private readonly ILogger<CostTrackingChatClient> _logger;
    private readonly IAgentCostLedger _ledger;

    public CostTrackingChatClient(IChatClient inner, ILogger<CostTrackingChatClient> logger) : base(inner)
    {
        _logger = logger;
        _ledger = new AgentCostLedger();
    }

    public CostTrackingChatClient(
        IChatClient inner,
        IAgentCostLedger ledger,
        ILogger<CostTrackingChatClient> logger) : base(inner)
    {
        _ledger = ledger;
        _logger = logger;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);

        if (response.Usage is { } usage)
        {
            var agentId = options?.AdditionalProperties?.GetValueOrDefault("agentId")?.ToString() ?? "unknown";
            var modelId = response.ModelId ?? "unknown";
            _ledger.RecordUsage(agentId, modelId, usage.InputTokenCount ?? 0, usage.OutputTokenCount ?? 0);

            LogTokenUsage(agentId, usage.InputTokenCount, usage.OutputTokenCount, modelId);
        }

        return response;
    }

    public AgentCostSummary? GetCostSummary(string agentId) => _ledger.GetCostSummary(agentId);

    public IReadOnlyDictionary<string, AgentCostSummary> GetAllCosts() => _ledger.GetAllCosts();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Agent {AgentId} used {Input} input + {Output} output tokens (model: {Model})")]
    private partial void LogTokenUsage(string agentId, long? input, long? output, string model);
}

public sealed class AgentCostSummary
{
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public int RequestCount { get; set; }
    public string LastModel { get; set; } = string.Empty;
}
