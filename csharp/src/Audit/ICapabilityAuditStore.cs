using Weave.Security.Events;

namespace Weave.Security.Audit;

/// <summary>
/// Append-only, queryable buffer of <see cref="CapabilityAuthorizationEvent"/>
/// rows. Powers the capability replay/debugger surface — given a tokenId,
/// callers can replay the chronological allow/deny trace for that token.
/// </summary>
/// <remarks>
/// The default in-memory implementation is bounded; entries beyond capacity
/// are evicted FIFO. Durable storage is a follow-up.
/// </remarks>
public interface ICapabilityAuditStore
{
    /// <summary>Records an authorization event.</summary>
    void Record(CapabilityAuthorizationEvent @event);

    /// <summary>
    /// Returns every recorded event for <paramref name="tokenId"/> in chronological order.
    /// Empty if the token never authorized through this silo or its rows have been evicted.
    /// </summary>
    IReadOnlyList<CapabilityAuthorizationEvent> GetByToken(string tokenId);

    /// <summary>
    /// Returns the most recent <paramref name="limit"/> events across all tokens, newest first.
    /// </summary>
    IReadOnlyList<CapabilityAuthorizationEvent> GetRecent(int limit);
}
