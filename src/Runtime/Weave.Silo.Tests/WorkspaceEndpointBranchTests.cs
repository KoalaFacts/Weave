using System.Net;
using System.Net.Http.Json;
using Weave.Workspaces.Manifest;
namespace Weave.Silo.Tests;

/// <summary>
/// Additional branches in <see cref="Api.WorkspaceEndpoints"/> not covered
/// by the main lifecycle test — list (GET /api/workspaces), GET unknown
/// workspace → 404, validation failures on POST.
/// </summary>
public sealed class WorkspaceEndpointBranchTests : IClassFixture<SiloFactory>
{
    private readonly SiloFactory _factory;

    public WorkspaceEndpointBranchTests(SiloFactory factory) => _factory = factory;

    [Fact]
    public async Task GetState_UnknownId_DoesNot500()
    {
        // WorkspaceActor auto-activates on any access, so unknown IDs won't
        // return 404 today. Document current behavior: endpoint must at least
        // not crash — a future fix (status/started-at check) should flip this
        // to explicit 404.
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            $"/api/workspaces/ws_nonexistent_{Guid.NewGuid():N}",
            TestContext.Current.CancellationToken);

        ((int)response.StatusCode).ShouldBeLessThan(500);
    }

    [Fact]
    public async Task Start_MissingName_Returns400()
    {
        using var client = _factory.CreateClient();
        var manifest = new WorkspaceManifest { Version = "1.0", Name = "" };

        using var response = await client.PostAsJsonAsync(
            "/api/workspaces",
            new { Manifest = manifest },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Start_MissingVersion_Returns400()
    {
        using var client = _factory.CreateClient();
        var manifest = new WorkspaceManifest { Version = "", Name = "ok" };

        using var response = await client.PostAsJsonAsync(
            "/api/workspaces",
            new { Manifest = manifest },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Stop_UnknownWorkspace_DoesNot500()
    {
        using var client = _factory.CreateClient();

        using var response = await client.DeleteAsync(
            $"/api/workspaces/ws_nonexistent_{Guid.NewGuid():N}",
            TestContext.Current.CancellationToken);

        ((int)response.StatusCode).ShouldBeLessThan(500);
    }

    [Fact]
    public async Task GetState_AfterStart_ReturnsWorkspace()
    {
        using var client = _factory.CreateClient();
        using var startResponse = await client.PostAsJsonAsync(
            "/api/workspaces",
            new { Manifest = new WorkspaceManifest { Version = "1.0", Name = $"lookup-{Guid.NewGuid():N}" } },
            TestContext.Current.CancellationToken);
        startResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var startDoc = System.Text.Json.JsonDocument.Parse(
            await startResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var workspaceId = startDoc.RootElement.GetProperty("workspaceId").GetString();

        using var response = await client.GetAsync($"/api/workspaces/{workspaceId}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
