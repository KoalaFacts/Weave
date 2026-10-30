using System.Globalization;
using Weave.Actions.Context;
using Weave.Actions.Marketplace;
using Weave.Actions.SystemInfo;

namespace Weave.Cli.Commands;

internal sealed class MarketplaceInfoCliCommand(
    GetSystemInfoAction systemInfoAction,
    BrowseMarketplaceItemsAction browseAction,
    GetMarketplaceItemAction getAction) : ICliCommand<MarketplaceInfoOptions>
{
    public string Name => "info";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Show details for a marketplace item";

    public async Task<int> ExecuteAsync(MarketplaceInfoOptions options, CancellationToken ct)
    {
        var systemInfo = await systemInfoAction.ExecuteAsync(new GetSystemInfoInput(), ct);
        if (!systemInfo.IsSuccess || !systemInfo.Value.Reachable)
        {
            CliTheme.WriteError("Weave server is not running. Start it with 'weave serve'.");
            return 1;
        }

        var itemId = await MarketplaceItemPrompt.SelectItemIdAsync(browseAction, options.ItemId, "Which marketplace item would you like to inspect?", ct);
        if (itemId is null)
        {
            CliTheme.WriteWarning("No marketplace items found.");
            return 0;
        }

        var result = await getAction.ExecuteAsync(new GetMarketplaceItemInput(itemId), ct);
        if (!result.IsSuccess)
        {
            if (result.Failure.Reason == ActionFailureReason.Cancelled)
                return 130;
            CliTheme.WriteError(result.Failure.Message);
            return 1;
        }

        var item = result.Value.Item;
        CliTheme.WriteSection(item.Name);
        CliTheme.WriteKeyValue("ID", item.ItemId);
        CliTheme.WriteKeyValue("Category", item.Category);
        CliTheme.WriteKeyValue("Version", item.Version);
        CliTheme.WriteKeyValue("Author", item.Author);
        CliTheme.WriteKeyValue("Status", item.Status);
        CliTheme.WriteKeyValue("Description", item.Description);
        CliTheme.WriteKeyValue("Installs", item.InstallCount.ToString(CultureInfo.InvariantCulture));

        if (item.RatingCount > 0)
            CliTheme.WriteKeyValue("Rating", $"{item.Rating:F1}/5 ({item.RatingCount} ratings)");

        if (item.Tags.Count > 0)
            CliTheme.WriteKeyValue("Tags", string.Join(", ", item.Tags));

        return 0;
    }
}
