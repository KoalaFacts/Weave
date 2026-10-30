using System.Net.Http.Json;
using Weave.Actions.Context;

namespace Weave.Actions.Marketplace;

public sealed record SearchMarketplaceInput(string Query);

public sealed record SearchMarketplaceResult(IReadOnlyList<MarketplaceItemSummary> Items);

public sealed class SearchMarketplaceAction(HttpClient httpClient)
{
    public async Task<ActionResult<SearchMarketplaceResult>> ExecuteAsync(
        SearchMarketplaceInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Query);

        try
        {
            var wire = await httpClient.GetFromJsonAsync(
                $"/api/marketplace/search?q={Uri.EscapeDataString(input.Query)}",
                MarketplaceJsonContext.Default.ListMarketplaceItemWire,
                cancellationToken) ?? [];

            return ActionResult.Success(new SearchMarketplaceResult(MarketplaceItemMapper.ToSummaries(wire)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<SearchMarketplaceResult>(ActionFailure.Cancelled());
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Failed<SearchMarketplaceResult>(
                ActionFailure.SiloUnreachable($"Silo unreachable: {ex.Message}"));
        }
    }
}
