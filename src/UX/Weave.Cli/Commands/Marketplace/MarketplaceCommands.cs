using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class MarketplaceCommands
{
    public static Command Create(
        MarketplaceListCliCommand listHandler,
        MarketplaceSearchCliCommand searchHandler,
        MarketplaceSubmitCliCommand submitHandler,
        MarketplacePublishCliCommand publishHandler,
        MarketplaceInfoCliCommand infoHandler,
        MarketplaceInstallCliCommand installHandler)
    {
        var cmd = new Command("marketplace", "Browse and manage the curated tool marketplace");

        cmd.Subcommands.Add(CreateListCommand(listHandler));
        cmd.Subcommands.Add(CreateSearchCommand(searchHandler));
        cmd.Subcommands.Add(CreateSubmitCommand(submitHandler));
        cmd.Subcommands.Add(CreatePublishCommand(publishHandler));
        cmd.Subcommands.Add(CreateInfoCommand(infoHandler));
        cmd.Subcommands.Add(CreateInstallCommand(installHandler));

        return cmd;
    }

    private static Command CreateInstallCommand(MarketplaceInstallCliCommand handler)
    {
        var itemIdArg = new Argument<string?>("item-id")
        {
            Description = "Marketplace item ID",
            Arity = ArgumentArity.ZeroOrOne
        };
        var cmd = new Command("install", "Install a marketplace item (capability-gated)") { itemIdArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var itemId = parseResult.GetValue(itemIdArg);
            return await handler.ExecuteAsync(new MarketplaceInstallOptions(itemId), cancellationToken);
        });

        return cmd;
    }

    private static Command CreateListCommand(MarketplaceListCliCommand handler)
    {
        var cmd = new Command("list", "List published marketplace items");
        cmd.SetAction((_, cancellationToken) => handler.ExecuteAsync(new NoCliOptions(), cancellationToken));

        return cmd;
    }

    private static Command CreateSearchCommand(MarketplaceSearchCliCommand handler)
    {
        var queryArg = new Argument<string?>("query")
        {
            Description = "Search query",
            Arity = ArgumentArity.ZeroOrOne
        };

        var cmd = new Command("search", "Search marketplace items") { queryArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var query = parseResult.GetValue(queryArg);
            return await handler.ExecuteAsync(new MarketplaceSearchOptions(query), cancellationToken);
        });

        return cmd;
    }

    private static Command CreateSubmitCommand(MarketplaceSubmitCliCommand handler)
    {
        var cmd = new Command("submit", "Submit a new item to the marketplace");
        cmd.SetAction((_, cancellationToken) => handler.ExecuteAsync(new NoCliOptions(), cancellationToken));

        return cmd;
    }

    private static Command CreatePublishCommand(MarketplacePublishCliCommand handler)
    {
        var itemIdArg = new Argument<string?>("item-id")
        {
            Description = "Marketplace item ID",
            Arity = ArgumentArity.ZeroOrOne
        };
        var cmd = new Command("publish", "Publish an item after security review") { itemIdArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var itemId = parseResult.GetValue(itemIdArg);
            return await handler.ExecuteAsync(new MarketplacePublishOptions(itemId), cancellationToken);
        });

        return cmd;
    }

    private static Command CreateInfoCommand(MarketplaceInfoCliCommand handler)
    {
        var itemIdArg = new Argument<string?>("item-id")
        {
            Description = "Marketplace item ID",
            Arity = ArgumentArity.ZeroOrOne
        };
        var cmd = new Command("info", "Show details for a marketplace item") { itemIdArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var itemId = parseResult.GetValue(itemIdArg);
            return await handler.ExecuteAsync(new MarketplaceInfoOptions(itemId), cancellationToken);
        });

        return cmd;
    }
}
