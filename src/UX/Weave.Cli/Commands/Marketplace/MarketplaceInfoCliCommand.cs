using System.Globalization;

namespace Weave.Cli.Commands;

internal sealed class MarketplaceInfoCliCommand : ICliCommand<MarketplaceInfoOptions>
{
    public string Name => "info";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Show details for a marketplace item";

    public async Task<int> ExecuteAsync(MarketplaceInfoOptions options, CancellationToken ct)
    {
        using var client = new MarketplaceApiClient();
        if (!await client.IsReachableAsync(ct))
        {
            CliTheme.WriteError("Weave server is not running. Start it with 'weave serve'.");
            return 1;
        }

        var itemId = await MarketplaceItemPrompt.SelectItemIdAsync(client, options.ItemId, "Which marketplace item would you like to inspect?", ct);
        if (itemId is null)
        {
            CliTheme.WriteWarning("No marketplace items found.");
            return 0;
        }

        var item = await client.GetItemAsync(itemId, ct);
        if (item is null)
        {
            CliTheme.WriteError($"Item '{itemId}' not found.");
            return 1;
        }

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
