using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Weave.Silo.Tests;

/// <summary>
/// End-to-end capability template tests — register → get → publish →
/// deprecate, plus validation and not-found paths. Templates are
/// globally-scoped (grain key "global"), so each test uses unique
/// names and is tolerant of existing state.
/// </summary>
public sealed class TemplateLifecycleTests : IClassFixture<SiloFactory>
{
    private static readonly string[] MissingToolList = ["ghost-tool"];

    private readonly SiloFactory _factory;

    public TemplateLifecycleTests(SiloFactory factory) => _factory = factory;

    private static object BuildRegisterBody(string? name = null) => new
    {
        Name = name ?? $"tpl-{Guid.NewGuid():N}",
        Description = "A test template.",
        Version = "1.0.0",
        Author = "tests",
        AgentDefinition = new { Model = "gpt-4o-mini" },
        Tags = new[] { "test" }
    };

    private static async Task<string> RegisterAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/templates",
            BuildRegisterBody(),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("templateId").GetString()!;
    }

    [Fact]
    public async Task Register_WithValidBody_Returns201()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/templates",
            BuildRegisterBody("register-happy"),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("name").GetString().ShouldBe("register-happy");
    }

    [Fact]
    public async Task Register_MissingFields_Returns400()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/templates",
            new
            {
                Name = "",
                Description = "",
                Version = "",
                Author = "",
                AgentDefinition = new { Model = "x" }
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_UnknownTemplate_Returns404()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/templates/tpl_missing",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_AfterRegister_ReturnsTemplate()
    {
        using var client = _factory.CreateClient();
        var id = await RegisterAsync(client);

        using var response = await client.GetAsync(
            $"/api/templates/{id}",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Search_WithQuery_Returns200()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/templates/search?q=test&max=5",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ListPublished_WithPagingParams_Returns200()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/templates?offset=0&limit=5",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Publish_UnknownTemplate_Returns404()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync(
            "/api/templates/tpl_missing/publish",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Deprecate_UnknownTemplate_Returns404()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync(
            "/api/templates/tpl_missing/deprecate",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Publish_ValidationFails_Returns422()
    {
        // CapabilityTemplate.ValidateAndPublishAsync reports a ValidationResult
        // for each unresolved tool reference. An agent that references a tool
        // not listed in RequiredTools fails the "ToolPresent:<name>" check —
        // so the template stays in Draft status and publish returns 422.
        using var client = _factory.CreateClient();

        using var registerResponse = await client.PostAsJsonAsync(
            "/api/templates",
            new
            {
                Name = $"invalid-{Guid.NewGuid():N}",
                Description = "agent references a missing tool",
                Version = "1.0.0",
                Author = "tests",
                AgentDefinition = new { Model = "gpt-4o-mini", Tools = MissingToolList }
                // RequiredTools intentionally omitted → ToolPresent validation fails
            },
            TestContext.Current.CancellationToken);
        registerResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(
            await registerResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var templateId = doc.RootElement.GetProperty("templateId").GetString();

        using var publishResponse = await client.PostAsync(
            $"/api/templates/{templateId}/publish",
            content: null,
            TestContext.Current.CancellationToken);

        publishResponse.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }
}
