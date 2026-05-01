using Spectre.Console;

namespace Weave.Cli.Commands;

internal sealed class MarketplaceSubmitCliCommand : ICliCommand<NoCliOptions>
{
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

        var item = await client.SubmitItemAsync(name, description, category, version, author, tags, ct);

        CliTheme.WriteSuccess($"Item '{item.Name}' submitted (ID: {item.ItemId}, status: {item.Status}).");
        CliTheme.WriteInfo("Submit a security review with 'weave marketplace publish' to make it available.");
        return 0;
    }
}
