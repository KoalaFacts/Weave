using Weave.Shared.Ids;
using Weave.Tools.Models;

namespace Weave.Silo.Api;

internal sealed class MarketplaceItemMapper
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from MarketplaceEndpoints.")]
    public MarketplaceItem FromRequest(SubmitMarketplaceItemRequest request)
    {
        return new MarketplaceItem
        {
            ItemId = MarketplaceItemId.New(),
            Name = request.Name,
            Description = request.Description,
            Category = request.Category,
            Version = request.Version,
            Author = request.Author,
            Tags = request.Tags ?? [],
            RequiredCapabilities = request.RequiredCapabilities ?? [],
            DocumentationUrl = request.DocumentationUrl
        };
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from MarketplaceEndpoints.")]
    public SecurityReview ReviewFromRequest(PublishMarketplaceItemRequest request)
    {
        return new SecurityReview
        {
            ReviewerId = request.ReviewerId,
            Approved = request.Approved,
            Notes = request.Notes
        };
    }
}
