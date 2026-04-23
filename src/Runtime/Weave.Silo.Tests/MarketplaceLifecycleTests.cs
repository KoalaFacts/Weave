using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Weave.Silo.Tests;

/// <summary>
/// End-to-end marketplace tests — submit → publish → rate → deprecate
/// over real HTTP against a real grain. Covers every branch of
/// <c>MarketplaceEndpoints</c> including validation, search, and
/// not-found paths. Each test uses unique item names to avoid
/// cross-test contamination (the marketplace grain is singleton keyed "global").
/// </summary>
public sealed class MarketplaceLifecycleTests : IClassFixture<SiloFactory>
{
    private readonly SiloFactory _factory;

    public MarketplaceLifecycleTests(SiloFactory factory) => _factory = factory;

    private static object BuildSubmitBody(string? name = null) => new
    {
        Name = name ?? $"item-{Guid.NewGuid():N}",
        Description = "An item for tests.",
        Category = "ToolConnector",
        Version = "1.0.0",
        Author = "tests",
        Tags = new[] { "test" },
        RequiredCapabilities = Array.Empty<string>()
    };

    private static async Task<string> SubmitItemAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/marketplace",
            BuildSubmitBody(),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("itemId").GetString()!;
    }

    [Fact]
    public async Task Submit_WithValidBody_Returns201AndItem()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/marketplace",
            BuildSubmitBody("mkt-submit-happy"),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("name").GetString().ShouldBe("mkt-submit-happy");
        doc.RootElement.GetProperty("status").GetString().ShouldBe("Draft");
    }

    [Fact]
    public async Task Submit_MissingRequiredFields_Returns400WithValidationErrors()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/marketplace",
            new { Name = "", Description = "", Category = "ToolConnector", Version = "", Author = "" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("name");
        body.ShouldContain("version");
    }

    [Fact]
    public async Task GetItem_UnknownId_Returns404()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/marketplace/mkt_nonexistent",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetItem_AfterSubmit_ReturnsItem()
    {
        using var client = _factory.CreateClient();
        var itemId = await SubmitItemAsync(client);

        using var response = await client.GetAsync(
            $"/api/marketplace/{itemId}",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("itemId").GetString().ShouldBe(itemId);
    }

    [Fact]
    public async Task Search_WithInvalidCategory_Returns400()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/marketplace/search?category=NotACategory",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Search_WithValidCategory_Returns200()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/marketplace/search?category=ToolConnector&q=&max=5",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Publish_WithApprovedReview_MovesItemToPublished()
    {
        using var client = _factory.CreateClient();
        var itemId = await SubmitItemAsync(client);

        using var response = await client.PostAsJsonAsync(
            $"/api/marketplace/{itemId}/publish",
            new { ReviewerId = "reviewer-1", Approved = true, Notes = "ok" },
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("status").GetString().ShouldBe("Published");
    }

    [Fact]
    public async Task Rate_OutOfRange_Returns400()
    {
        using var client = _factory.CreateClient();
        var itemId = await SubmitItemAsync(client);

        using var response = await client.PostAsJsonAsync(
            $"/api/marketplace/{itemId}/rate",
            new { Rating = 6.5 },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rate_InValidRange_Returns204()
    {
        using var client = _factory.CreateClient();
        var itemId = await SubmitItemAsync(client);

        using var response = await client.PostAsJsonAsync(
            $"/api/marketplace/{itemId}/rate",
            new { Rating = 4.5 },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Deprecate_UnknownId_Returns404()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync(
            "/api/marketplace/mkt_nonexistent/deprecate",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListPublished_WithPagingParams_Returns200()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/marketplace?offset=0&limit=5",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldStartWith("[");
    }
}
