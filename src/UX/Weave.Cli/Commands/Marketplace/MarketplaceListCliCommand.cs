using System.Globalization;
using Spectre.Console;
using Weave.Actions.Context;
using Weave.Actions.Marketplace;
using Weave.Actions.SystemInfo;

namespace Weave.Cli.Commands;

internal sealed class MarketplaceListCliCommand(
    GetSystemInfoAction systemInfoAction,
    BrowseMarketplaceItemsAction browseAction) : ICliCommand<NoCliOptions>
{
    public string Name => "list";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "List published marketplace items";

    public async Task<int> ExecuteAsync(NoCliOptions options, CancellationToken ct)
    {
        var systemInfo = await systemInfoAction.ExecuteAsync(new GetSystemInfoInput(), ct);
        if (!systemInfo.IsSuccess || !systemInfo.Value.Reachable)
        {
            CliTheme.WriteError("Weave server is not running. Start it with 'weave serve'.");
            return 1;
        }

        var result = await browseAction.ExecuteAsync(new BrowseMarketplaceItemsInput(), ct);
        if (!result.IsSuccess)
        {
            if (result.Failure.Reason == ActionFailureReason.Cancelled)
                return 130;
            CliTheme.WriteError(result.Failure.Message);
            return 1;
        }

        if (result.Value.Items.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No published items in the marketplace.[/]");
            return 0;
        }

        var table = CliTheme.CreateTable("Marketplace");
        table.AddColumn(CliTheme.StyledColumn("Name"));
        table.AddColumn(CliTheme.StyledColumn("Category"));
        table.AddColumn(CliTheme.StyledColumn("Version"));
        table.AddColumn(CliTheme.StyledColumn("Author"));
        table.AddColumn(CliTheme.StyledColumn("Rating"));
        table.AddColumn(CliTheme.StyledColumn("Installs"));

        foreach (var item in result.Value.Items)
        {
            var rating = item.RatingCount > 0 ? $"{item.Rating:F1} ({item.RatingCount})" : "[dim]—[/]";
            table.AddRow(
                Markup.Escape(item.Name),
                Markup.Escape(item.Category),
                Markup.Escape(item.Version),
                Markup.Escape(item.Author),
                rating,
                item.InstallCount.ToString(CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);
        return 0;
    }
}
