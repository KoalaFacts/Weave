using System.Net.Http.Json;
using Weave.Actions.Context;

namespace Weave.Actions.Audit;

public sealed record GetCapabilityAuditByTokenInput(string TokenId);

public sealed record GetCapabilityAuditByTokenResult(IReadOnlyList<CapabilityAuditEntry> Entries);

public sealed class GetCapabilityAuditByTokenAction(HttpClient httpClient)
{
    public async Task<ActionResult<GetCapabilityAuditByTokenResult>> ExecuteAsync(
        GetCapabilityAuditByTokenInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.TokenId);

        try
        {
            var wire = await httpClient.GetFromJsonAsync(
                $"/api/audit/capability/{Uri.EscapeDataString(input.TokenId)}",
                AuditJsonContext.Default.ListCapabilityAuditEntryWire,
                cancellationToken) ?? [];

            return ActionResult.Success(new GetCapabilityAuditByTokenResult(AuditEntryMapper.ToEntries(wire)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<GetCapabilityAuditByTokenResult>(ActionFailure.Cancelled());
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Failed<GetCapabilityAuditByTokenResult>(
                ActionFailure.SiloUnreachable($"Silo unreachable: {ex.Message}"));
        }
    }
}
