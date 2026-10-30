using Spectre.Console;
using Weave.Actions.Context;
using Weave.Actions.Marketplace;
using Weave.Actions.SystemInfo;

namespace Weave.Cli.Commands;

internal sealed class MarketplacePublishCliCommand(
    GetSystemInfoAction systemInfoAction,
    BrowseMarketplaceItemsAction browseAction,
    PublishMarketplaceItemAction publishAction) : ICliCommand<MarketplacePublishOptions>
{
    public string Name => "publish";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Publish an item after security review";

    public async Task<int> ExecuteAsync(MarketplacePublishOptions options, CancellationToken ct)
    {
        var systemInfo = await systemInfoAction.ExecuteAsync(new GetSystemInfoInput(), ct);
        if (!systemInfo.IsSuccess || !systemInfo.Value.Reachable)
        {
            CliTheme.WriteError("Weave server is not running. Start it with 'weave serve'.");
            return 1;
        }

        var itemId = await MarketplaceItemPrompt.SelectItemIdAsync(browseAction, options.ItemId, "Which marketplace item would you like to review?", ct);
        if (itemId is null)
        {
            CliTheme.WriteWarning("No marketplace items found.");
            return 0;
        }

        var review = PromptReview();

        var result = await publishAction.ExecuteAsync(
            new PublishMarketplaceItemInput(itemId, review.ReviewerId, review.Approved, review.Notes),
            ct);

        if (!result.IsSuccess)
        {
            if (result.Failure.Reason == ActionFailureReason.Cancelled)
                return 130;
            CliTheme.WriteError($"Failed to publish: {result.Failure.Message}");
            return 1;
        }

        var item = result.Value.Item;
        if (item.Status == "Published")
            CliTheme.WriteSuccess($"Item '{item.Name}' is now published in the marketplace.");
        else
            CliTheme.WriteWarning($"Item '{item.Name}' was not published (status: {item.Status}).");
        return 0;
    }

    private static MarketplaceReview PromptReview()
    {
        var reviewerId = AnsiConsole.Prompt(new TextPrompt<string>("Reviewer ID:").Styled());
        var approved = AnsiConsole.Confirm("Approve for publishing?");
        var notes = AnsiConsole.Prompt(new TextPrompt<string>("Review notes (optional):").Styled().AllowEmpty());

        return new MarketplaceReview(
            reviewerId,
            approved,
            string.IsNullOrWhiteSpace(notes) ? null : notes);
    }
}
