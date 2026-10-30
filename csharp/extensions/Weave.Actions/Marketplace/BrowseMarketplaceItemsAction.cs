using System.Net.Http.Json;
using Weave.Actions.Context;

namespace Weave.Actions.Marketplace;

public sealed record BrowseMarketplaceItemsInput;

public sealed record BrowseMarketplaceItemsResult(IReadOnlyList<MarketplaceItemSummary> Items);

/// <summary>
/// User-facing typed list of marketplace items. Distinct from
/// <see cref="ListMarketplaceItemsAction"/> (Phase 4b, opaque
/// <see cref="System.Text.Json.JsonElement"/>) which exists for workspace
/// export wire fidelity.
/// </summary>
public sealed class BrowseMarketplaceItemsAction(HttpClient httpClient)
{
    public async Task<ActionResult<BrowseMarketplaceItemsResult>> ExecuteAsync(
        BrowseMarketplaceItemsInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            var wire = await httpClient.GetFromJsonAsync(
                "/api/marketplace",
                MarketplaceJsonContext.Default.ListMarketplaceItemWire,
                cancellationToken) ?? [];

            return ActionResult.Success(new BrowseMarketplaceItemsResult(MarketplaceItemMapper.ToSummaries(wire)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<BrowseMarketplaceItemsResult>(ActionFailure.Cancelled());
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Failed<BrowseMarketplaceItemsResult>(
                ActionFailure.SiloUnreachable($"Silo unreachable: {ex.Message}"));
        }
    }
}
