using System.Net;
using System.Text.Json;

namespace Weave.Silo.Tests.Plugins;

public sealed partial class DaprWorkspacePluginFlowTests
{
    [Fact]
    public async Task GetInstallationInventory_DaprWorkspace_ShowsConnectionWithoutConfiguration()
    {
        await using var sidecar = CreateSidecar("inventory");
        await sidecar.StartAsync(TestContext.Current.CancellationToken);
        using var client = _factory.CreateClient();
        var workspaceId = await StartWorkspaceAsync(client, _factory.Services, sidecar);

        using var response = await SendAuthorizedAsync(client, _factory.Services, HttpMethod.Get,
            $"/api/plugins/installations/{workspaceId}", workspaceId, "plugin:installations:read");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);
        var installation = document.RootElement.EnumerateArray().Single();
        installation.GetProperty("id").GetString().ShouldBe($"{workspaceId}/sidecar");
        installation.GetProperty("pluginName").GetString().ShouldBe("sidecar");
        installation.GetProperty("type").GetString().ShouldBe("dapr_tools");
        installation.GetProperty("desiredEnabled").GetBoolean().ShouldBeTrue();
        installation.GetProperty("runtimeConnected").GetBoolean().ShouldBeTrue();
        installation.GetProperty("condition").GetString().ShouldBe("ready");
        installation.GetProperty("reasonCode").ValueKind.ShouldBe(JsonValueKind.Null);
        installation.GetProperty("lastCheckedAt").GetDateTimeOffset()
            .ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow);
        body.ShouldNotContain("port");
        body.ShouldNotContain("configDigest");
    }
}
