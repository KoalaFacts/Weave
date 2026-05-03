using Spectre.Console;

namespace Weave.Cli.Commands;

internal sealed class MarketplacePublishCliCommand : ICliCommand<MarketplacePublishOptions>
{
    public string Name => "publish";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Publish an item after security review";

    public async Task<int> ExecuteAsync(MarketplacePublishOptions options, CancellationToken ct)
    {
        using var client = new MarketplaceApiClient();
        if (!await client.IsReachableAsync(ct))
        {
            CliTheme.WriteError("Weave server is not running. Start it with 'weave serve'.");
            return 1;
        }

        var itemId = await MarketplaceItemPrompt.SelectItemIdAsync(client, options.ItemId, "Which marketplace item would you like to review?", ct);
        if (itemId is null)
        {
            CliTheme.WriteWarning("No marketplace items found.");
            return 0;
        }

        var review = PromptReview();

        try
        {
            var item = await client.PublishItemAsync(
                itemId,
                review.ReviewerId,
                review.Approved,
                review.Notes,
                ct);

            if (item.Status == "Published")
                CliTheme.WriteSuccess($"Item '{item.Name}' is now published in the marketplace.");
            else
                CliTheme.WriteWarning($"Item '{item.Name}' was not published (status: {item.Status}).");
        }
        catch (HttpRequestException ex)
        {
            CliTheme.WriteError($"Failed to publish: {ex.Message}");
            return 1;
        }

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
