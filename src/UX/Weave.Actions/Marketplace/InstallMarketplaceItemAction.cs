using System.Net.Http.Json;
using Weave.Actions.Context;
using Weave.Workspaces.Manifest;

namespace Weave.Actions.Marketplace;

public sealed record InstallMarketplaceItemInput(string ItemId);

public sealed record InstallMarketplaceItemResult(
    MarketplaceItemSummary Item,
    InstalledMarketplaceTemplate Template);

public sealed class InstallMarketplaceItemAction(HttpClient httpClient)
{
    public async Task<ActionResult<InstallMarketplaceItemResult>> ExecuteAsync(
        InstallMarketplaceItemInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.ItemId);

        try
        {
            using var response = await httpClient.PostAsync(
                $"/api/marketplace/{Uri.EscapeDataString(input.ItemId)}/install",
                content: null,
                cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return ActionResult.Failed<InstallMarketplaceItemResult>(
                    ActionFailure.NotFound($"Item '{input.ItemId}' not found."));

            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                return ActionResult.Failed<InstallMarketplaceItemResult>(
                    ActionFailure.Conflict(string.IsNullOrWhiteSpace(detail) ? "Install rejected." : detail));
            }

            if (!response.IsSuccessStatusCode)
            {
                return ActionResult.Failed<InstallMarketplaceItemResult>(response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                        ActionFailure.Unauthorized($"Silo refused install: {(int)response.StatusCode}."),
                    _ => ActionFailure.Internal($"Silo returned {(int)response.StatusCode}.")
                });
            }

            var wire = await response.Content.ReadFromJsonAsync(
                MarketplaceJsonContext.Default.InstallMarketplaceWire,
                cancellationToken)
                ?? throw new InvalidOperationException("Marketplace API returned an empty install response.");

            return ActionResult.Success(new InstallMarketplaceItemResult(
                MarketplaceItemMapper.ToSummary(wire.Item),
                ToTemplate(wire.Template)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<InstallMarketplaceItemResult>(ActionFailure.Cancelled());
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Failed<InstallMarketplaceItemResult>(
                ActionFailure.SiloUnreachable($"Silo unreachable: {ex.Message}"));
        }
    }

    private static InstalledMarketplaceTemplate ToTemplate(InstallTemplateWire w) =>
        new()
        {
            TemplateId = w.TemplateId,
            Name = w.Name,
            Description = w.Description,
            Version = w.Version,
            Author = w.Author,
            Status = w.Status,
            Tags = w.Tags ?? [],
            InstantiationCount = w.InstantiationCount,
            AgentDefinition = w.AgentDefinition
                ?? throw new InvalidOperationException("Install response missing AgentDefinition."),
            RequiredTools = w.RequiredTools ?? new Dictionary<string, ToolDefinition>()
        };
}
