using System.Net.Http.Json;
using Weave.Actions.Context;

namespace Weave.Actions.Marketplace;

/// <summary>
/// Phase 4b verb. Lists silo-wide marketplace items as opaque JSON, used by
/// workspace export to roundtrip the silo's current marketplace catalog.
/// </summary>
public sealed class ListMarketplaceItemsAction(HttpClient httpClient)
{
    public async Task<ActionResult<ListMarketplaceItemsResult>> ExecuteAsync(
        ListMarketplaceItemsInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            var items = await httpClient.GetFromJsonAsync(
                "/api/marketplace",
                MarketplaceJsonContext.Default.ListJsonElement,
                cancellationToken) ?? [];

            return ActionResult.Success(new ListMarketplaceItemsResult(items));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<ListMarketplaceItemsResult>(ActionFailure.Cancelled());
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Failed<ListMarketplaceItemsResult>(
                ActionFailure.SiloUnreachable($"Silo unreachable: {ex.Message}"));
        }
    }
}
