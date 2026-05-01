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

        var reviewerId = AnsiConsole.Prompt(new TextPrompt<string>("Reviewer ID:").Styled());
        var approved = AnsiConsole.Confirm("Approve for publishing?");
        var notes = AnsiConsole.Prompt(new TextPrompt<string>("Review notes (optional):").Styled().AllowEmpty());

        try
        {
            var item = await client.PublishItemAsync(
                options.ItemId,
                reviewerId,
                approved,
                string.IsNullOrWhiteSpace(notes) ? null : notes,
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
