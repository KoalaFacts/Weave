using Spectre.Console;
using Weave.Actions.Context;
using Weave.Actions.Marketplace;
using Weave.Actions.SystemInfo;

namespace Weave.Cli.Commands;

internal sealed class MarketplaceSearchCliCommand(
    GetSystemInfoAction systemInfoAction,
    SearchMarketplaceAction searchAction) : ICliCommand<MarketplaceSearchOptions>
{
    public string Name => "search";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Search marketplace items";

    public async Task<int> ExecuteAsync(MarketplaceSearchOptions options, CancellationToken ct)
    {
        var query = options.Query;
        if (string.IsNullOrWhiteSpace(query))
            query = AnsiConsole.Prompt(new TextPrompt<string>("Search query:").Styled());

        var systemInfo = await systemInfoAction.ExecuteAsync(new GetSystemInfoInput(), ct);
        if (!systemInfo.IsSuccess || !systemInfo.Value.Reachable)
        {
            CliTheme.WriteError("Weave server is not running. Start it with 'weave serve'.");
            return 1;
        }

        var result = await searchAction.ExecuteAsync(new SearchMarketplaceInput(query), ct);
        if (!result.IsSuccess)
        {
            if (result.Failure.Reason == ActionFailureReason.Cancelled)
                return 130;
            CliTheme.WriteError(result.Failure.Message);
            return 1;
        }

        if (result.Value.Items.Count == 0)
        {
            CliTheme.WriteWarning($"No marketplace items matching '{query}'.");
            return 0;
        }

        var table = CliTheme.CreateTable($"Results for \"{query}\"");
        table.AddColumn(CliTheme.StyledColumn("Name"));
        table.AddColumn(CliTheme.StyledColumn("Category"));
        table.AddColumn(CliTheme.StyledColumn("Description"));
        table.AddColumn(CliTheme.StyledColumn("Author"));

        foreach (var item in result.Value.Items)
        {
            var desc = item.Description.Length > 60 ? item.Description[..57] + "..." : item.Description;
            table.AddRow(Markup.Escape(item.Name), Markup.Escape(item.Category), Markup.Escape(desc), Markup.Escape(item.Author));
        }

        AnsiConsole.Write(table);
        return 0;
    }
}
