using Spectre.Console;

namespace Weave.Cli.Commands;

internal sealed class MarketplaceSearchCliCommand : ICliCommand<MarketplaceSearchOptions>
{
    public string Name => "search";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Search marketplace items";

    public async Task<int> ExecuteAsync(MarketplaceSearchOptions options, CancellationToken ct)
    {
        var query = options.Query;
        if (string.IsNullOrWhiteSpace(query))
            query = AnsiConsole.Prompt(new TextPrompt<string>("Search query:").Styled());

        using var client = new MarketplaceApiClient();
        if (!await client.IsReachableAsync(ct))
        {
            CliTheme.WriteError("Weave server is not running. Start it with 'weave serve'.");
            return 1;
        }

        var items = await client.SearchAsync(query, ct);
        if (items.Count == 0)
        {
            CliTheme.WriteWarning($"No marketplace items matching '{query}'.");
            return 0;
        }

        var table = CliTheme.CreateTable($"Results for \"{query}\"");
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
