using Spectre.Console;

namespace Weave.Cli.Commands;

internal sealed class MarketplaceSearchCliCommand : ICliCommand<MarketplaceSearchOptions>
{
    public string Name => "search";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Search marketplace items";

    public async Task<int> ExecuteAsync(MarketplaceSearchOptions options, CancellationToken ct)
    {
        using var client = new WorkspaceApiClient();
        if (!await client.IsReachableAsync(ct))
        {
            CliTheme.WriteError("Weave server is not running. Start it with 'weave serve'.");
            return 1;
        }

        var items = await client.SearchMarketplaceAsync(options.Query, ct);
        if (items.Count == 0)
        {
            CliTheme.WriteWarning($"No marketplace items matching '{options.Query}'.");
            return 0;
        }

        var table = CliTheme.CreateTable($"Results for \"{options.Query}\"");
        table.AddColumn(CliTheme.StyledColumn("Name"));
        table.AddColumn(CliTheme.StyledColumn("Category"));
        table.AddColumn(CliTheme.StyledColumn("Description"));
        table.AddColumn(CliTheme.StyledColumn("Author"));

        foreach (var item in items)
        {
            var desc = item.Description.Length > 60 ? item.Description[..57] + "..." : item.Description;
            table.AddRow(Markup.Escape(item.Name), Markup.Escape(item.Category), Markup.Escape(desc), Markup.Escape(item.Author));
        }

        AnsiConsole.Write(table);
        return 0;
    }
}
