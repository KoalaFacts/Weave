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

        var item = await client.GetItemAsync(options.ItemId, ct);
        if (item is null)
        {
            CliTheme.WriteError($"Item '{options.ItemId}' not found.");
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
