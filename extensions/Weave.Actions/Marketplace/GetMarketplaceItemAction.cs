using System.Net.Http.Json;
using Weave.Actions.Context;

namespace Weave.Actions.Marketplace;

public sealed record GetMarketplaceItemInput(string ItemId);

public sealed record GetMarketplaceItemResult(MarketplaceItemSummary Item);

public sealed class GetMarketplaceItemAction(HttpClient httpClient)
{
    public async Task<ActionResult<GetMarketplaceItemResult>> ExecuteAsync(
        GetMarketplaceItemInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.ItemId);

        try
        {
            using var response = await httpClient.GetAsync(
                $"/api/marketplace/{Uri.EscapeDataString(input.ItemId)}",
                cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return ActionResult.Failed<GetMarketplaceItemResult>(
                    ActionFailure.NotFound($"Item '{input.ItemId}' not found."));

            if (!response.IsSuccessStatusCode)
                return ActionResult.Failed<GetMarketplaceItemResult>(
                    ActionFailure.Internal($"Silo returned {(int)response.StatusCode}."));

            var wire = await response.Content.ReadFromJsonAsync(
                MarketplaceJsonContext.Default.MarketplaceItemWire,
                cancellationToken)
                ?? throw new InvalidOperationException("Marketplace API returned an empty payload.");

            return ActionResult.Success(new GetMarketplaceItemResult(MarketplaceItemMapper.ToSummary(wire)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<GetMarketplaceItemResult>(ActionFailure.Cancelled());
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Failed<GetMarketplaceItemResult>(
                ActionFailure.SiloUnreachable($"Silo unreachable: {ex.Message}"));
        }
    }
}
