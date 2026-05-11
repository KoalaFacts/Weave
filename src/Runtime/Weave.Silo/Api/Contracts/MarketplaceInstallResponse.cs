using Weave.Tools.Marketplace;
namespace Weave.Silo.Api;

public sealed record MarketplaceInstallResponse
{
    public required MarketplaceItemResponse Item { get; init; }
    public required TemplateResponse Template { get; init; }

    public static MarketplaceInstallResponse FromResult(MarketplaceInstallResult result) => new()
    {
        Item = MarketplaceItemResponse.FromItem(result.Item),
        Template = TemplateResponse.FromTemplate(result.Template)
    };
}
