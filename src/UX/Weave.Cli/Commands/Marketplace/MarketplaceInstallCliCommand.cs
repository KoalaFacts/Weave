using System.Globalization;

namespace Weave.Cli.Commands;

internal sealed class MarketplaceInstallCliCommand : ICliCommand<MarketplaceInstallOptions>
{
    public string Name => "install";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Install a marketplace item — capability-gated; resolves the linked template.";

    public async Task<int> ExecuteAsync(MarketplaceInstallOptions options, CancellationToken ct)
    {
        using var client = new MarketplaceApiClient();
        if (!await client.IsReachableAsync(ct))
        {
            CliTheme.WriteError("Weave server is not running. Start it with 'weave serve'.");
            return 1;
        }

        var itemId = await MarketplaceItemPrompt.SelectItemIdAsync(
            client, options.ItemId, "Which marketplace item would you like to install?", ct);
        if (itemId is null)
        {
            CliTheme.WriteWarning("No marketplace items found.");
            return 0;
        }

        ApiMarketplaceInstallResponse? result;
        try
        {
            result = await client.InstallAsync(itemId, ct);
        }
        catch (InvalidOperationException ex)
        {
            CliTheme.WriteError($"Install rejected: {ex.Message}");
            return 1;
        }

        if (result is null)
        {
            CliTheme.WriteError($"Item '{itemId}' not found.");
            return 1;
        }

        CliTheme.WriteSuccess($"Installed: {result.Item.Name}");
        CliTheme.WriteKeyValue("Item ID", result.Item.ItemId);
        CliTheme.WriteKeyValue("Install count", result.Item.InstallCount.ToString(CultureInfo.InvariantCulture));
        CliTheme.WriteKeyValue("Resolved template", $"{result.Template.Name} ({result.Template.TemplateId})");
        CliTheme.WriteKeyValue("Template version", result.Template.Version);
        CliTheme.WriteKeyValue("Template instantiations", result.Template.InstantiationCount.ToString(CultureInfo.InvariantCulture));

        if (result.Template.Tags.Count > 0)
            CliTheme.WriteKeyValue("Tags", string.Join(", ", result.Template.Tags));

        CliTheme.WriteMuted(string.Empty);
        CliTheme.WriteMuted($"  The capability check passed; the template registry recorded the install. To scaffold");
        CliTheme.WriteMuted($"  a workspace from this template locally, run 'weave workspace new --preset <name>'.");
        return 0;
    }
}
