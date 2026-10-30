using Spectre.Console;
using Weave.Actions.Marketplace;

namespace Weave.Cli.Commands;

internal static class MarketplaceItemPrompt
{
    public static async Task<string?> SelectItemIdAsync(
        BrowseMarketplaceItemsAction browseAction,
        string? itemId,
        string title,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(itemId))
            return itemId;

        var result = await browseAction.ExecuteAsync(new BrowseMarketplaceItemsInput(), ct);
        if (!result.IsSuccess || result.Value.Items.Count == 0)
            return null;

        var choices = result.Value.Items.ToDictionary(
            item => $"{item.Name} ({item.ItemId})",
            item => item.ItemId,
            StringComparer.Ordinal);

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title(title)
                .Styled()
                .AddChoices(choices.Keys));

        return choices[selected];
    }
}
