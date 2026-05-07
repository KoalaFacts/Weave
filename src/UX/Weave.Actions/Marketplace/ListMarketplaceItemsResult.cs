using System.Text.Json;

namespace Weave.Actions.Marketplace;

/// <summary>
/// Marketplace items returned as opaque <see cref="JsonElement"/> for the
/// same wire-fidelity reason as the workspace export's other JSON lists.
/// A future user-facing "list marketplace items" command can introduce a
/// typed <c>MarketplaceItemSummary</c> action without affecting this one.
/// </summary>
public sealed record ListMarketplaceItemsResult(IReadOnlyList<JsonElement> Items);
