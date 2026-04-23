using System.Net;

namespace Weave.Silo.Tests;

/// <summary>
/// Dedicated isolated fixture for GET /api/workspaces. Builds its own
/// <see cref="SiloFactory"/> per test run so grain contamination from
/// other test classes (which share a <c>IClassFixture&lt;SiloFactory&gt;</c>)
/// can't leak registered workspace IDs into the list handler and push it
/// into a 500.
/// </summary>
public sealed class WorkspaceListEndpointTests : IDisposable
{
    private readonly SiloFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task ListAll_FreshSilo_Returns200WithJsonArray()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/workspaces", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldStartWith("[");
    }
}
