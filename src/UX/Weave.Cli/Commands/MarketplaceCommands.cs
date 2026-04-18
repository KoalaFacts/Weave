using System.CommandLine;
using System.Net.Http.Json;
using Spectre.Console;

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
        cmd.SetAction(async (_, cancellationToken) =>
        {
            using var client = new WorkspaceApiClient();

            if (!await client.IsReachableAsync(cancellationToken))
            {
                CliTheme.WriteError("Weave server is not running. Start it with 'weave serve'.");
                return 1;
            }

            var items = await client.GetMarketplaceItemsAsync(cancellationToken);

            if (items.Count == 0)
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

            foreach (var item in items)
            {
                var rating = item.RatingCount > 0
                    ? $"{item.Rating:F1} ({item.RatingCount})"
                    : "[dim]—[/]";

                table.AddRow(
                    Markup.Escape(item.Name),
                    Markup.Escape(item.Category),
                    Markup.Escape(item.Version),
                    Markup.Escape(item.Author),
                    rating,
                    item.InstallCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            AnsiConsole.Write(table);
            return 0;
        });

        return cmd;
    }

    private static Command CreateSearchCommand()
    {
        var queryArg = new Argument<string>("query") { Description = "Search query" };

        var cmd = new Command("search", "Search marketplace items") { queryArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var query = parseResult.GetValue(queryArg)!;
            using var client = new WorkspaceApiClient();

            if (!await client.IsReachableAsync(cancellationToken))
            {
                CliTheme.WriteError("Weave server is not running. Start it with 'weave serve'.");
                return 1;
            }

            var items = await client.SearchMarketplaceAsync(query, cancellationToken);

            if (items.Count == 0)
            {
                CliTheme.WriteWarning($"No marketplace items matching '{query}'.");
                return 0;
            }

            var table = CliTheme.CreateTable($"Results for \"{query}\"");
            table.AddColumn(CliTheme.StyledColumn("Name"));
            table.AddColumn(CliTheme.StyledColumn("Category"));
            table.AddColumn(CliTheme.StyledColumn("Description"));
            table.AddColumn(CliTheme.StyledColumn("Author"));

            foreach (var item in items)
            {
                var desc = item.Description.Length > 60
                    ? item.Description[..57] + "..."
                    : item.Description;

                table.AddRow(
                    Markup.Escape(item.Name),
                    Markup.Escape(item.Category),
                    Markup.Escape(desc),
                    Markup.Escape(item.Author));
            }

            AnsiConsole.Write(table);
            return 0;
        });

        return cmd;
    }

    private static Command CreateSubmitCommand()
    {
        var cmd = new Command("submit", "Submit a new item to the marketplace");
        cmd.SetAction(async (_, cancellationToken) =>
        {
            using var client = new WorkspaceApiClient();

            if (!await client.IsReachableAsync(cancellationToken))
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

            var item = await client.SubmitMarketplaceItemAsync(
                name, description, category, version, author, tags, cancellationToken);

            CliTheme.WriteSuccess($"Item '{item.Name}' submitted (ID: {item.ItemId}, status: {item.Status}).");
            CliTheme.WriteInfo("Submit a security review with 'weave marketplace publish' to make it available.");
            return 0;
        });

        return cmd;
    }

    private static Command CreatePublishCommand()
    {
        var itemIdArg = new Argument<string>("item-id") { Description = "Marketplace item ID" };
        var cmd = new Command("publish", "Publish an item after security review") { itemIdArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var itemId = parseResult.GetValue(itemIdArg)!;
            using var client = new WorkspaceApiClient();

            if (!await client.IsReachableAsync(cancellationToken))
            {
                CliTheme.WriteError("Weave server is not running. Start it with 'weave serve'.");
                return 1;
            }

            var reviewerId = AnsiConsole.Prompt(new TextPrompt<string>("Reviewer ID:").Styled());
            var approved = AnsiConsole.Confirm("Approve for publishing?");
            var notes = AnsiConsole.Prompt(new TextPrompt<string>("Review notes (optional):").Styled().AllowEmpty());

            try
            {
                var item = await client.PublishMarketplaceItemAsync(
                    itemId, reviewerId, approved, string.IsNullOrWhiteSpace(notes) ? null : notes, cancellationToken);

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
            using var client = new WorkspaceApiClient();

            if (!await client.IsReachableAsync(cancellationToken))
            {
                CliTheme.WriteError("Weave server is not running. Start it with 'weave serve'.");
                return 1;
            }

            var item = await client.GetMarketplaceItemAsync(itemId, cancellationToken);
            if (item is null)
            {
                CliTheme.WriteError($"Item '{itemId}' not found.");
                return 1;
            }

            CliTheme.WriteSection(item.Name);
            CliTheme.WriteKeyValue("ID", item.ItemId);
            CliTheme.WriteKeyValue("Category", item.Category);
            CliTheme.WriteKeyValue("Version", item.Version);
            CliTheme.WriteKeyValue("Author", item.Author);
            CliTheme.WriteKeyValue("Status", item.Status);
            CliTheme.WriteKeyValue("Description", item.Description);
            CliTheme.WriteKeyValue("Installs", item.InstallCount.ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (item.RatingCount > 0)
                CliTheme.WriteKeyValue("Rating", $"{item.Rating:F1}/5 ({item.RatingCount} ratings)");

            if (item.Tags.Count > 0)
                CliTheme.WriteKeyValue("Tags", string.Join(", ", item.Tags));

            return 0;
        });

        return cmd;
    }
}
