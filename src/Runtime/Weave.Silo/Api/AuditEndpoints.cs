using Weave.Security.Events;
using Weave.Security.Queries;
using Weave.Shared.Cqrs;

namespace Weave.Silo.Api;

public static class AuditEndpoints
{
    public static RouteGroupBuilder MapAuditEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/audit/capability")
            .WithTags("Audit");

        group.MapGet("/", GetRecentAsync)
            .WithDescription("Most recent capability authorization rows across all tokens, newest first.")
            .Produces<IEnumerable<CapabilityAuditEntryResponse>>();

        group.MapGet("/{tokenId}", GetByTokenAsync)
            .WithDescription("Replay every authorization decision (allow + deny) for a given capability token, in chronological order.")
            .Produces<IEnumerable<CapabilityAuditEntryResponse>>();

        return group;
    }

    private static async Task<IResult> GetRecentAsync(
        int? limit,
        IQueryDispatcher dispatcher,
        CancellationToken ct)
    {
        var query = new GetRecentCapabilityAuditQuery(limit ?? 100);
        var rows = await dispatcher.DispatchAsync<GetRecentCapabilityAuditQuery, IReadOnlyList<CapabilityAuthorizationEvent>>(query, ct);
        return Results.Ok(rows.Select(CapabilityAuditEntryResponse.From));
    }

    private static async Task<IResult> GetByTokenAsync(
        string tokenId,
        IQueryDispatcher dispatcher,
        CancellationToken ct)
    {
        var query = new GetCapabilityAuditByTokenQuery(tokenId);
        var rows = await dispatcher.DispatchAsync<GetCapabilityAuditByTokenQuery, IReadOnlyList<CapabilityAuthorizationEvent>>(query, ct);
        return Results.Ok(rows.Select(CapabilityAuditEntryResponse.From));
    }
}

public sealed record CapabilityAuditEntryResponse(
    string TokenId,
    string Grant,
    string IssuedTo,
    string WorkspaceId,
    string ActionContext,
    string Outcome,
    string? Reason,
    DateTimeOffset Timestamp)
{
    public static CapabilityAuditEntryResponse From(CapabilityAuthorizationEvent evt) =>
        new(
            evt.TokenId,
            evt.Grant,
            evt.IssuedTo,
            evt.WorkspaceId,
            evt.ActionContext,
            evt.Outcome.ToString(),
            evt.Reason,
            evt.Timestamp);
}
