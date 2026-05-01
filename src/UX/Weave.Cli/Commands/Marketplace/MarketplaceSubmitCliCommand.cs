namespace Weave.Cli.Commands;

internal sealed class MarketplaceSubmitCliCommand(MarketplaceSubmissionPrompt? prompt = null) : ICliCommand<NoCliOptions>
{
    private readonly MarketplaceSubmissionPrompt _prompt = prompt ?? new MarketplaceSubmissionPrompt();

    public string Name => "submit";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Submit a new item to the marketplace";

    public async Task<int> ExecuteAsync(NoCliOptions options, CancellationToken ct)
    {
        using var client = new MarketplaceApiClient();
        if (!await client.IsReachableAsync(ct))
        {
            CliTheme.WriteError("Weave server is not running. Start it with 'weave serve'.");
            return 1;
        }

        var submission = _prompt.Prompt();
        var item = await client.SubmitItemAsync(
            submission.Name,
            submission.Description,
            submission.Category,
            submission.Version,
            submission.Author,
            submission.Tags,
            ct);

        CliTheme.WriteSuccess($"Item '{item.Name}' submitted (ID: {item.ItemId}, status: {item.Status}).");
        CliTheme.WriteInfo("Submit a security review with 'weave marketplace publish' to make it available.");
        return 0;
    }
}
