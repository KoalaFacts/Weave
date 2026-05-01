using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class MarketplaceCommands
{
    public static Command Create()
    {
        var cmd = new Command("marketplace", "Browse and manage the curated tool marketplace");

        cmd.Subcommands.Add(CreateListCommand());
        cmd.Subcommands.Add(CreateSearchCommand());
        cmd.Subcommands.Add(CreateSubmitCommand());
        cmd.Subcommands.Add(CreatePublishCommand());
        cmd.Subcommands.Add(CreateInfoCommand());

        return cmd;
    }

    private static Command CreateListCommand()
    {
        var cmd = new Command("list", "List published marketplace items");
        cmd.SetAction((_, cancellationToken) => new MarketplaceListCliCommand().ExecuteAsync(new NoCliOptions(), cancellationToken));

        return cmd;
    }

    private static Command CreateSearchCommand()
    {
        var queryArg = new Argument<string>("query") { Description = "Search query" };

        var cmd = new Command("search", "Search marketplace items") { queryArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var query = parseResult.GetValue(queryArg)!;
            return await new MarketplaceSearchCliCommand().ExecuteAsync(new MarketplaceSearchOptions(query), cancellationToken);
        });

        return cmd;
    }

    private static Command CreateSubmitCommand()
    {
        var cmd = new Command("submit", "Submit a new item to the marketplace");
        cmd.SetAction((_, cancellationToken) => new MarketplaceSubmitCliCommand().ExecuteAsync(new NoCliOptions(), cancellationToken));

        return cmd;
    }

    private static Command CreatePublishCommand()
    {
        var itemIdArg = new Argument<string>("item-id") { Description = "Marketplace item ID" };
        var cmd = new Command("publish", "Publish an item after security review") { itemIdArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var itemId = parseResult.GetValue(itemIdArg)!;
            return await new MarketplacePublishCliCommand().ExecuteAsync(new MarketplacePublishOptions(itemId), cancellationToken);
        });

        return cmd;
    }

    private static Command CreateInfoCommand()
    {
        var itemIdArg = new Argument<string>("item-id") { Description = "Marketplace item ID" };
        var cmd = new Command("info", "Show details for a marketplace item") { itemIdArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var itemId = parseResult.GetValue(itemIdArg)!;
            return await new MarketplaceInfoCliCommand().ExecuteAsync(new MarketplaceInfoOptions(itemId), cancellationToken);
        });

        return cmd;
    }
}
