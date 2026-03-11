using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Weave.Agents.Pipeline;

/// <summary>
/// IChatClient middleware that tracks token usage and estimated cost per agent.
/// </summary>
public sealed class CostTrackingChatClient : DelegatingChatClient
{
    private readonly ILogger<CostTrackingChatClient> _logger;
    private readonly ConcurrentDictionary<string, AgentCostSummary> _costs = new();

    public CostTrackingChatClient(IChatClient inner, ILogger<CostTrackingChatClient> logger) : base(inner)
    {
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

            var summary = _costs.GetOrAdd(agentId, _ => new AgentCostSummary());
            summary.TotalInputTokens += usage.InputTokenCount ?? 0;
            summary.TotalOutputTokens += usage.OutputTokenCount ?? 0;
            summary.RequestCount++;
            summary.LastModel = modelId;

            _logger.LogDebug(
                "Agent {AgentId} used {Input} input + {Output} output tokens (model: {Model})",
                agentId, usage.InputTokenCount, usage.OutputTokenCount, modelId);
        }

        return response;
    }

    public AgentCostSummary? GetCostSummary(string agentId) =>
        _costs.TryGetValue(agentId, out var summary) ? summary : null;

    public IReadOnlyDictionary<string, AgentCostSummary> GetAllCosts() => _costs;
}

public sealed class AgentCostSummary
{
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public int RequestCount { get; set; }
    public string LastModel { get; set; } = string.Empty;
}
