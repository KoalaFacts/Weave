using Weave.Security.Audit;
using Weave.Security.Events;
using Weave.Shared.Cqrs;

namespace Weave.Security.Queries;

public sealed record GetRecentCapabilityAuditQuery(int Limit);

public sealed class GetRecentCapabilityAuditHandler(ICapabilityAuditStore store)
    : IQueryHandler<GetRecentCapabilityAuditQuery, IReadOnlyList<CapabilityAuthorizationEvent>>
{
    public Task<IReadOnlyList<CapabilityAuthorizationEvent>> HandleAsync(
        GetRecentCapabilityAuditQuery query,
        CancellationToken ct) =>
        Task.FromResult(store.GetRecent(query.Limit));
}
