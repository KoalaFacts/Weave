namespace Weave.Silo.Api;

internal sealed class MarketplaceRequestValidator
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from MarketplaceEndpoints.")]
    public Dictionary<string, string[]>? ValidateSubmit(SubmitMarketplaceItemRequest request)
    {
        Dictionary<string, string[]>? errors = null;

        if (string.IsNullOrWhiteSpace(request.Name))
            (errors ??= [])["name"] = ["Name is required."];
        if (string.IsNullOrWhiteSpace(request.Description))
            (errors ??= [])["description"] = ["Description is required."];
        if (string.IsNullOrWhiteSpace(request.Version))
            (errors ??= [])["version"] = ["Version is required."];
        if (string.IsNullOrWhiteSpace(request.Author))
            (errors ??= [])["author"] = ["Author is required."];

        return errors;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from MarketplaceEndpoints.")]
    public Dictionary<string, string[]>? ValidateRating(RateMarketplaceItemRequest request)
    {
        return request.Rating is < 0.0 or > 5.0
            ? new Dictionary<string, string[]> { ["rating"] = ["Rating must be between 0.0 and 5.0."] }
            : null;
    }
}
