using Spectre.Console;
using Weave.Actions.Context;
using Weave.Actions.Marketplace;
using Weave.Actions.SystemInfo;

namespace Weave.Cli.Commands;

internal sealed class MarketplaceSubmitCliCommand(
    GetSystemInfoAction systemInfoAction,
    SubmitMarketplaceItemAction submitAction) : ICliCommand<NoCliOptions>
{
    public string Name => "submit";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Submit a new item to the marketplace";

    public async Task<int> ExecuteAsync(NoCliOptions options, CancellationToken ct)
    {
        var systemInfo = await systemInfoAction.ExecuteAsync(new GetSystemInfoInput(), ct);
        if (!systemInfo.IsSuccess || !systemInfo.Value.Reachable)
        {
            CliTheme.WriteError("Weave server is not running. Start it with 'weave serve'.");
            return 1;
        }

        var submission = PromptSubmission();
        var result = await submitAction.ExecuteAsync(
            new SubmitMarketplaceItemInput(
                submission.Name,
                submission.Description,
                submission.Category,
                submission.Version,
                submission.Author,
                submission.Tags),
            ct);

        if (!result.IsSuccess)
        {
            if (result.Failure.Reason == ActionFailureReason.Cancelled)
                return 130;
            CliTheme.WriteError(result.Failure.Message);
            return 1;
        }

        var item = result.Value.Item;
        CliTheme.WriteSuccess($"Item '{item.Name}' submitted (ID: {item.ItemId}, status: {item.Status}).");
        CliTheme.WriteInfo("Submit a security review with 'weave marketplace publish' to make it available.");
        return 0;
    }

    private static MarketplaceSubmission PromptSubmission()
    {
        var name = AnsiConsole.Prompt(new TextPrompt<string>("Item name:").Styled());
        var description = AnsiConsole.Prompt(new TextPrompt<string>("Description:").Styled());
        var category = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Category:")
                .Styled()
                .AddChoices("ToolConnector", "AgentSkill", "ToolChain", "Integration"));
        var version = AnsiConsole.Prompt(new TextPrompt<string>("Version:").Styled().DefaultValue("1.0.0"));
        var author = AnsiConsole.Prompt(new TextPrompt<string>("Author:").Styled());
        var tagsInput = AnsiConsole.Prompt(new TextPrompt<string>("Tags (comma-separated):").Styled().AllowEmpty());

        var tags = string.IsNullOrWhiteSpace(tagsInput)
            ? Array.Empty<string>()
            : tagsInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new MarketplaceSubmission(name, description, category, version, author, tags);
    }
}
