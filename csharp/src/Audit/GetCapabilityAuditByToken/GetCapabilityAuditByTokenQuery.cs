using Weave.Security.Audit;
using Weave.Security.Events;
using Weave.Shared.Cqrs;

namespace Weave.Security.Queries;

public sealed record GetCapabilityAuditByTokenQuery(string TokenId);

public sealed class GetCapabilityAuditByTokenHandler(ICapabilityAuditStore store)
    : IQueryHandler<GetCapabilityAuditByTokenQuery, IReadOnlyList<CapabilityAuthorizationEvent>>
{
    public Task<IReadOnlyList<CapabilityAuthorizationEvent>> HandleAsync(
        GetCapabilityAuditByTokenQuery query,
        CancellationToken ct) =>
        Task.FromResult(store.GetByToken(query.TokenId));
}
