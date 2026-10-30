using System.Net;

namespace Weave.Silo.Tests;

/// <summary>
/// Branch tests for <see cref="Api.ToolEndpoints"/> — list tools + GET
/// unknown tool 404 path.
/// </summary>
public sealed class ToolEndpointTests : IClassFixture<SiloFactory>
{
    private readonly SiloFactory _factory;

    public ToolEndpointTests(SiloFactory factory) => _factory = factory;

    [Fact]
    public async Task ListTools_NewWorkspace_Returns200WithArray()
    {
        using var client = _factory.CreateClient();
        var ws = $"ws-{Guid.NewGuid():N}";

        using var response = await client.GetAsync(
            $"/api/workspaces/{ws}/tools",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldStartWith("[");
    }

    [Fact]
    public async Task GetTool_UnknownTool_Returns404()
    {
        using var client = _factory.CreateClient();
        var ws = $"ws-{Guid.NewGuid():N}";

        using var response = await client.GetAsync(
            $"/api/workspaces/{ws}/tools/never-connected",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
