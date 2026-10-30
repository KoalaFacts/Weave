using System.Net.Http.Json;
using Weave.Actions.Context;

namespace Weave.Actions.Marketplace;

public sealed record SubmitMarketplaceItemInput(
    string Name,
    string Description,
    string Category,
    string Version,
    string Author,
    IReadOnlyList<string> Tags);

public sealed record SubmitMarketplaceItemResult(MarketplaceItemSummary Item);

public sealed class SubmitMarketplaceItemAction(HttpClient httpClient)
{
    public async Task<ActionResult<SubmitMarketplaceItemResult>> ExecuteAsync(
        SubmitMarketplaceItemInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "/api/marketplace",
                new SubmitMarketplaceWire
                {
                    Name = input.Name,
                    Description = input.Description,
                    Category = input.Category,
                    Version = input.Version,
                    Author = input.Author,
                    Tags = input.Tags
                },
                MarketplaceJsonContext.Default.SubmitMarketplaceWire,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return ActionResult.Failed<SubmitMarketplaceItemResult>(response.StatusCode switch
                {
                    System.Net.HttpStatusCode.BadRequest =>
                        ActionFailure.ValidationFailed($"Marketplace rejected submission: {(int)response.StatusCode}."),
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                        ActionFailure.Unauthorized($"Silo refused submission: {(int)response.StatusCode}."),
                    _ => ActionFailure.Internal($"Silo returned {(int)response.StatusCode}.")
                });
            }

            var wire = await response.Content.ReadFromJsonAsync(
                MarketplaceJsonContext.Default.MarketplaceItemWire,
                cancellationToken)
                ?? throw new InvalidOperationException("Marketplace API returned an empty submission response.");

            return ActionResult.Success(new SubmitMarketplaceItemResult(MarketplaceItemMapper.ToSummary(wire)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<SubmitMarketplaceItemResult>(ActionFailure.Cancelled());
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Failed<SubmitMarketplaceItemResult>(
                ActionFailure.SiloUnreachable($"Silo unreachable: {ex.Message}"));
        }
    }
}
