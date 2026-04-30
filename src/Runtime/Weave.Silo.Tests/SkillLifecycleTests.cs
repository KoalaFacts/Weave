using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Weave.Silo.Tests;

/// <summary>
/// End-to-end skill tests — store → get → search → remove over real HTTP.
/// Covers validation branches in <c>SkillEndpoints.ValidateStoreSkill</c>
/// and the KeyNotFoundException → 404 translation in GetSkillAsync.
/// </summary>
public sealed class SkillLifecycleTests : IClassFixture<SiloFactory>
{
    private readonly SiloFactory _factory;

    public SkillLifecycleTests(SiloFactory factory) => _factory = factory;

    private static string NewWorkspaceId() => $"ws-{Guid.NewGuid():N}";

    private static object BuildStoreBody(string title = "how-to-x") => new
    {
        Title = title,
        Description = "Steps for x.",
        Tags = new[] { "t1" },
        Steps = new[] { new { Action = "do thing", ToolName = "bash", ExpectedOutcome = "ok" } },
        ToolsUsed = new[] { "bash" },
        CreatedByAgent = "agent-1",
        OriginTaskDescription = "a task"
    };

    private static async Task<string> StoreSkillAsync(HttpClient client, string workspaceId)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/skills",
            BuildStoreBody(),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("skillId").GetString()!;
    }

    [Fact]
    public async Task Store_WithValidBody_Returns201AndSkill()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/skills",
            BuildStoreBody("first-skill"),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("title").GetString().ShouldBe("first-skill");
        doc.RootElement.GetProperty("skillId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Store_MissingTitle_Returns400()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/skills",
            new
            {
                Title = "",
                Description = "d",
                Steps = new[] { new { Action = "a" } },
                CreatedByAgent = "agent-1"
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Store_EmptySteps_Returns400()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/skills",
            new { Title = "t", Description = "d", Steps = Array.Empty<object>(), CreatedByAgent = "a" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_UnknownSkill_Returns404()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.GetAsync(
            $"/api/workspaces/{ws}/skills/skl_missing",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_AfterStore_ReturnsSkill()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();
        var id = await StoreSkillAsync(client, ws);

        using var response = await client.GetAsync(
            $"/api/workspaces/{ws}/skills/{id}",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task List_NewWorkspace_Returns200()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.GetAsync(
            $"/api/workspaces/{ws}/skills",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Search_WithQueryAndMax_Returns200()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.GetAsync(
            $"/api/workspaces/{ws}/skills/search?q=test&max=3",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Search_WithQualityOptions_Returns200()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.GetAsync(
            $"/api/workspaces/{ws}/skills/search?q=test&max=3&minSuccessRate=0.75&preferRecent=true",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Suggestions_NewWorkspace_Returns200()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.GetAsync(
            $"/api/workspaces/{ws}/skills/suggestions",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AcceptSuggestion_UnknownSuggestion_Returns404()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsync(
            $"/api/workspaces/{ws}/skills/suggestions/skl_missing/accept",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RejectSuggestion_UnknownSuggestion_Returns404()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsync(
            $"/api/workspaces/{ws}/skills/suggestions/skl_missing/reject",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Archive_AfterStore_ReturnsArchivedSkill()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();
        var id = await StoreSkillAsync(client, ws);

        using var response = await client.PostAsync(
            $"/api/workspaces/{ws}/skills/{id}/archive",
            content: null,
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("archivedAt").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Archive_UnknownSkill_Returns404()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsync(
            $"/api/workspaces/{ws}/skills/skl_missing/archive",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Restore_AfterArchive_ReturnsActiveSkill()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();
        var id = await StoreSkillAsync(client, ws);
        using var archiveResponse = await client.PostAsync(
            $"/api/workspaces/{ws}/skills/{id}/archive",
            content: null,
            TestContext.Current.CancellationToken);
        archiveResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var response = await client.PostAsync(
            $"/api/workspaces/{ws}/skills/{id}/restore",
            content: null,
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("archivedAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Restore_UnknownSkill_Returns404()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsync(
            $"/api/workspaces/{ws}/skills/skl_missing/restore",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Remove_AfterStore_Returns204()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();
        var id = await StoreSkillAsync(client, ws);

        using var response = await client.DeleteAsync(
            $"/api/workspaces/{ws}/skills/{id}",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}
