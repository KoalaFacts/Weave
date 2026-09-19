using System.Globalization;
using System.Net.Http.Json;
using Weave.Actions.Context;

namespace Weave.Actions.Audit;

public sealed record GetRecentCapabilityAuditInput(int Limit);

public sealed record GetRecentCapabilityAuditResult(IReadOnlyList<CapabilityAuditEntry> Entries);

public sealed class GetRecentCapabilityAuditAction(HttpClient httpClient)
{
    public async Task<ActionResult<GetRecentCapabilityAuditResult>> ExecuteAsync(
        GetRecentCapabilityAuditInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            var wire = await httpClient.GetFromJsonAsync(
                $"/api/audit/capability?limit={input.Limit.ToString(CultureInfo.InvariantCulture)}",
                AuditJsonContext.Default.ListCapabilityAuditEntryWire,
                cancellationToken) ?? [];

            return ActionResult.Success(new GetRecentCapabilityAuditResult(AuditEntryMapper.ToEntries(wire)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<GetRecentCapabilityAuditResult>(ActionFailure.Cancelled());
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Failed<GetRecentCapabilityAuditResult>(
                ActionFailure.SiloUnreachable($"Silo unreachable: {ex.Message}"));
        }
    }
}
