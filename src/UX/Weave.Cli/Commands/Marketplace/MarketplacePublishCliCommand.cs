namespace Weave.Cli.Commands;

internal sealed class MarketplacePublishCliCommand(MarketplaceReviewPrompt? prompt = null) : ICliCommand<MarketplacePublishOptions>
{
    private readonly MarketplaceReviewPrompt _prompt = prompt ?? new MarketplaceReviewPrompt();

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

        var review = _prompt.Prompt();

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
}
