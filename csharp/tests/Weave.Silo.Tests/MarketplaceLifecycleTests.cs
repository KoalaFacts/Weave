using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Weave.Silo.Tests;

/// <summary>
/// End-to-end marketplace tests — submit → publish → rate → deprecate
/// over real HTTP against a real actor. Covers every branch of
/// <c>MarketplaceEndpoints</c> including validation, search, and
/// not-found paths. Each test uses unique item names to avoid
/// cross-test contamination (the marketplace actor is singleton keyed "global").
/// </summary>
public sealed class MarketplaceLifecycleTests : IClassFixture<SiloFactory>
{
    private static readonly string[] TestTags = ["test"];

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

    [Fact]
    public async Task Install_PublishedItemWithLinkedTemplate_Returns200WithResolvedTemplate()
    {
        using var client = _factory.CreateClient();

        // The seeder publishes BuiltInTemplates at silo startup; pick one as the
        // install target. Submit + publish a marketplace item linked to it.
        var templateId = "tpl-built-in-coding-assistant";
        var name = $"mkt-install-{Guid.NewGuid():N}";

        using var submitResponse = await client.PostAsJsonAsync(
            "/api/marketplace",
            new
            {
                Name = name,
                Description = "links to coding-assistant",
                Category = "ToolConnector",
                Version = "1.0.0",
                Author = "tests",
                Tags = TestTags,
                RequiredCapabilities = Array.Empty<string>(),
                TemplateId = templateId
            },
            TestContext.Current.CancellationToken);
        submitResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var submitDoc = JsonDocument.Parse(
            await submitResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var itemId = submitDoc.RootElement.GetProperty("itemId").GetString()!;

        using var publishResponse = await client.PostAsJsonAsync(
            $"/api/marketplace/{itemId}/publish",
            new { ReviewerId = "reviewer", Approved = true, Notes = "ok" },
            TestContext.Current.CancellationToken);
        publishResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var installResponse = await client.PostAsync(
            $"/api/marketplace/{itemId}/install",
            content: null,
            TestContext.Current.CancellationToken);
        var installBody = await installResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        installResponse.StatusCode.ShouldBe(HttpStatusCode.OK, installBody);

        using var doc = JsonDocument.Parse(installBody);
        doc.RootElement.GetProperty("item").GetProperty("itemId").GetString().ShouldBe(itemId);
        doc.RootElement.GetProperty("item").GetProperty("installCount").GetInt32().ShouldBe(1);
        doc.RootElement.GetProperty("template").GetProperty("templateId").GetString().ShouldBe(templateId);
        doc.RootElement.GetProperty("template").GetProperty("name").GetString().ShouldBe("coding-assistant");
    }

    [Fact]
    public async Task Install_DraftItem_Returns409()
    {
        using var client = _factory.CreateClient();
        var itemId = await SubmitItemAsync(client);  // submitted, not published

        using var response = await client.PostAsync(
            $"/api/marketplace/{itemId}/install",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Install_UnknownItem_Returns404()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync(
            "/api/marketplace/mkt_install_missing/install",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Install_PublishedItemWithoutTemplate_Returns409()
    {
        using var client = _factory.CreateClient();
        var itemId = await SubmitItemAsync(client);

        using var publishResponse = await client.PostAsJsonAsync(
            $"/api/marketplace/{itemId}/publish",
            new { ReviewerId = "reviewer", Approved = true, Notes = "ok" },
            TestContext.Current.CancellationToken);
        publishResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var response = await client.PostAsync(
            $"/api/marketplace/{itemId}/install",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }
}
