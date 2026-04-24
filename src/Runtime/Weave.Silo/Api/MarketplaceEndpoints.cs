using Weave.Shared.Ids;
using Weave.Shared.VirtualActors;
using Weave.Tools.Actors;
using Weave.Tools.Models;

namespace Weave.Silo.Api;

public static class MarketplaceEndpoints
{
    public static RouteGroupBuilder MapMarketplaceEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/marketplace")
            .WithTags("Marketplace");

        group.MapGet("/", GetPublishedAsync)
            .WithDescription("List published marketplace items.")
            .Produces<IEnumerable<MarketplaceItemResponse>>();
        group.MapGet("/search", SearchAsync)
            .WithDescription("Search marketplace items.")
            .Produces<IEnumerable<MarketplaceItemResponse>>();
        group.MapGet("/{itemId}", GetItemAsync)
            .WithDescription("Get a marketplace item by ID.")
            .Produces<MarketplaceItemResponse>()
            .ProducesProblem(404);
        group.MapPost("/", SubmitAsync)
            .WithDescription("Submit a new item to the marketplace.")
            .Produces<MarketplaceItemResponse>(201)
            .ProducesValidationProblem();
        group.MapPost("/{itemId}/publish", PublishAsync)
            .WithDescription("Publish an item after security review.")
            .Produces<MarketplaceItemResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(404);
        group.MapPost("/{itemId}/rate", RateAsync)
            .WithDescription("Rate a marketplace item.")
            .Produces(204)
            .ProducesValidationProblem();
        group.MapPost("/{itemId}/deprecate", DeprecateAsync)
            .WithDescription("Deprecate a marketplace item.")
            .Produces(204)
            .ProducesProblem(404);

        return group;
    }

    private static async Task<IResult> GetPublishedAsync(
        int? offset,
        int? limit,
        IVirtualActorProvider actors,
        CancellationToken ct)
    {
        var actor = GetMarketplace(actors);
        var items = await actor.GetPublishedAsync(offset ?? 0, limit ?? 50);
        return Results.Ok(items.Select(MarketplaceItemResponse.FromItem));
    }

    private static async Task<IResult> SearchAsync(
        string? q,
        string? category,
        int? max,
        IVirtualActorProvider actors,
        CancellationToken ct)
    {
        MarketplaceItemCategory? cat = null;
        if (!string.IsNullOrWhiteSpace(category))
        {
            if (!Enum.TryParse<MarketplaceItemCategory>(category, ignoreCase: true, out var parsed))
                return ResultExtensions.ValidationFailed(new Dictionary<string, string[]>
                {
                    ["category"] = [$"'{category}' is not a valid category."]
                });
            cat = parsed;
        }

        var actor = GetMarketplace(actors);
        var items = await actor.SearchAsync(q, cat, max ?? 20);
        return Results.Ok(items.Select(MarketplaceItemResponse.FromItem));
    }

    private static async Task<IResult> GetItemAsync(
        string itemId,
        IVirtualActorProvider actors,
        CancellationToken ct)
    {
        var actor = GetMarketplace(actors);
        var item = await actor.GetAsync(MarketplaceItemId.From(itemId));
        if (item is null)
            return ResultExtensions.NotFound($"Marketplace item '{itemId}' not found.");

        return Results.Ok(MarketplaceItemResponse.FromItem(item));
    }

    private static async Task<IResult> SubmitAsync(
        SubmitMarketplaceItemRequest request,
        IVirtualActorProvider actors,
        CancellationToken ct)
    {
        var errors = ValidateSubmit(request);
        if (errors is not null)
            return ResultExtensions.ValidationFailed(errors);

        var item = new MarketplaceItem
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

        var actor = GetMarketplace(actors);
        var stored = await actor.SubmitAsync(item);
        return Results.Created($"/api/marketplace/{stored.ItemId}", MarketplaceItemResponse.FromItem(stored));
    }

    private static async Task<IResult> PublishAsync(
        string itemId,
        PublishMarketplaceItemRequest request,
        IVirtualActorProvider actors,
        CancellationToken ct)
    {
        var review = new SecurityReview
        {
            ReviewerId = request.ReviewerId,
            Approved = request.Approved,
            Notes = request.Notes
        };

        try
        {
            var actor = GetMarketplace(actors);
            var item = await actor.PublishAsync(MarketplaceItemId.From(itemId), review);
            return Results.Ok(MarketplaceItemResponse.FromItem(item));
        }
        catch (InvalidOperationException ex)
        {
            return ResultExtensions.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> RateAsync(
        string itemId,
        RateMarketplaceItemRequest request,
        IVirtualActorProvider actors,
        CancellationToken ct)
    {
        if (request.Rating is < 0.0 or > 5.0)
            return ResultExtensions.ValidationFailed(new Dictionary<string, string[]>
            {
                ["rating"] = ["Rating must be between 0.0 and 5.0."]
            });

        var actor = GetMarketplace(actors);
        await actor.RateAsync(MarketplaceItemId.From(itemId), request.Rating);
        return Results.NoContent();
    }

    private static async Task<IResult> DeprecateAsync(
        string itemId,
        IVirtualActorProvider actors,
        CancellationToken ct)
    {
        try
        {
            var actor = GetMarketplace(actors);
            await actor.DeprecateAsync(MarketplaceItemId.From(itemId));
            return Results.NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return ResultExtensions.NotFound(ex.Message);
        }
    }

    private static Dictionary<string, string[]>? ValidateSubmit(SubmitMarketplaceItemRequest request)
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

    private static IMarketplaceActor GetMarketplace(IVirtualActorProvider actors) =>
        actors.GetActor<IMarketplaceActor>(VirtualActorId.From("global"));
}
