using System.Threading.RateLimiting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Weave.Agents.Pipeline;

/// <summary>
/// IChatClient middleware that rate-limits LLM calls per agent.
/// </summary>
public sealed partial class RateLimitingChatClient : DelegatingChatClient
{
    private readonly RateLimiter _limiter;
    private readonly ILogger<RateLimitingChatClient> _logger;

    public RateLimitingChatClient(
        IChatClient inner,
        int maxRequestsPerMinute,
        ILogger<RateLimitingChatClient> logger) : base(inner)
    {
        _logger = logger;
        _limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = maxRequestsPerMinute,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            TokensPerPeriod = maxRequestsPerMinute,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 10
        });
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        using var lease = await _limiter.AcquireAsync(1, cancellationToken);
        if (!lease.IsAcquired)
        {
            LogRateLimitExceeded();
            throw new InvalidOperationException("LLM rate limit exceeded. Please try again later.");
        }

        return await base.GetResponseAsync(messages, options, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit exceeded for LLM request")]
    private partial void LogRateLimitExceeded();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _limiter.Dispose();
        base.Dispose(disposing);
    }
}
