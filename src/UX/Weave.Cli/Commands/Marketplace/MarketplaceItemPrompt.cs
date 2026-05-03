using Spectre.Console;

namespace Weave.Cli.Commands;

internal static class MarketplaceItemPrompt
{
    public static async Task<string?> SelectItemIdAsync(MarketplaceApiClient client, string? itemId, string title, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(itemId))
            return itemId;

        var items = await client.GetItemsAsync(ct);
        if (items.Count == 0)
            return null;

        var choices = items.ToDictionary(
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
