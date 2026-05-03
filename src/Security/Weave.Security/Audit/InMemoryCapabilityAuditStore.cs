using Microsoft.Extensions.Options;
using Weave.Security.Events;

namespace Weave.Security.Audit;

/// <summary>
/// In-memory <see cref="ICapabilityAuditStore"/>. Bounded capacity, FIFO
/// eviction. Thread-safe via a single lock — write/query rates for an
/// audit log are well below contention thresholds.
/// </summary>
public sealed class InMemoryCapabilityAuditStore : ICapabilityAuditStore
{
    private readonly LinkedList<CapabilityAuthorizationEvent> _events = new();
    private readonly Lock _gate = new();
    private readonly int _capacity;

    public InMemoryCapabilityAuditStore(IOptions<CapabilityAuditOptions> options)
    {
        var resolved = options.Value ?? throw new ArgumentNullException(nameof(options));
        if (resolved.Capacity <= 0)
            throw new InvalidOperationException(
                $"CapabilityAudit:Capacity must be greater than zero. Current value: {resolved.Capacity}.");
        _capacity = resolved.Capacity;
    }

    public void Record(CapabilityAuthorizationEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        lock (_gate)
        {
            _events.AddLast(@event);
            while (_events.Count > _capacity)
                _events.RemoveFirst();
        }
    }

    public IReadOnlyList<CapabilityAuthorizationEvent> GetByToken(string tokenId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenId);

        lock (_gate)
        {
            return _events
                .Where(e => string.Equals(e.TokenId, tokenId, StringComparison.Ordinal))
                .ToList();
        }
    }

    public IReadOnlyList<CapabilityAuthorizationEvent> GetRecent(int limit)
    {
        if (limit <= 0)
            return [];

        lock (_gate)
        {
            // Most recent first — walk the list from the tail.
            var result = new List<CapabilityAuthorizationEvent>(Math.Min(limit, _events.Count));
            var node = _events.Last;
            while (node is not null && result.Count < limit)
            {
                result.Add(node.Value);
                node = node.Previous;
            }
            return result;
        }
    }
}
