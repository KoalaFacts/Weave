using System.Net.Http.Json;
using Weave.Actions.Context;

namespace Weave.Actions.Marketplace;

public sealed record PublishMarketplaceItemInput(
    string ItemId,
    string ReviewerId,
    bool Approved,
    string? Notes);

public sealed record PublishMarketplaceItemResult(MarketplaceItemSummary Item);

public sealed class PublishMarketplaceItemAction(HttpClient httpClient)
{
    public async Task<ActionResult<PublishMarketplaceItemResult>> ExecuteAsync(
        PublishMarketplaceItemInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.ItemId);

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"/api/marketplace/{Uri.EscapeDataString(input.ItemId)}/publish",
                new PublishMarketplaceWire
                {
                    ReviewerId = input.ReviewerId,
                    Approved = input.Approved,
                    Notes = input.Notes
                },
                MarketplaceJsonContext.Default.PublishMarketplaceWire,
                cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return ActionResult.Failed<PublishMarketplaceItemResult>(
                    ActionFailure.NotFound($"Item '{input.ItemId}' not found."));

            if (!response.IsSuccessStatusCode)
            {
                return ActionResult.Failed<PublishMarketplaceItemResult>(response.StatusCode switch
                {
                    System.Net.HttpStatusCode.BadRequest =>
                        ActionFailure.ValidationFailed($"Marketplace rejected publish: {(int)response.StatusCode}."),
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                        ActionFailure.Unauthorized($"Silo refused publish: {(int)response.StatusCode}."),
                    _ => ActionFailure.Internal($"Silo returned {(int)response.StatusCode}.")
                });
            }

            var wire = await response.Content.ReadFromJsonAsync(
                MarketplaceJsonContext.Default.MarketplaceItemWire,
                cancellationToken)
                ?? throw new InvalidOperationException("Marketplace API returned an empty publish response.");

            return ActionResult.Success(new PublishMarketplaceItemResult(MarketplaceItemMapper.ToSummary(wire)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<PublishMarketplaceItemResult>(ActionFailure.Cancelled());
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Failed<PublishMarketplaceItemResult>(
                ActionFailure.SiloUnreachable($"Silo unreachable: {ex.Message}"));
        }
    }
}
