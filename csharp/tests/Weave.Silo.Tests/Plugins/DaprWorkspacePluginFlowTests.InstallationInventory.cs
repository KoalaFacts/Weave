using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Weave.Silo.Plugins;

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

    [Fact]
    public async Task ProbeOnceAsync_DaprSidecarChangesHealth_UpdatesSafeReadinessWithoutDisconnecting()
    {
        var healthy = true;
        await using var sidecar = CreateSidecar("probe", () => healthy);
        await sidecar.StartAsync(TestContext.Current.CancellationToken);
        using var client = _factory.CreateClient();
        var workspaceId = await StartWorkspaceAsync(client, _factory.Services, sidecar);
        var monitor = _factory.Services.GetRequiredService<ToolInstallationReadinessMonitor>();

        await monitor.ProbeOnceAsync(TestContext.Current.CancellationToken);
        using (var response = await SendAuthorizedAsync(client, _factory.Services, HttpMethod.Get,
            $"/api/plugins/installations/{workspaceId}", workspaceId, "plugin:installations:read"))
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
            var installation = document.RootElement.EnumerateArray().Single();
            installation.GetProperty("probeCondition").GetString().ShouldBe("responding");
            installation.GetProperty("probeReasonCode").ValueKind.ShouldBe(JsonValueKind.Null);
            installation.GetProperty("probeCheckedAt").GetDateTimeOffset().ShouldBeLessThanOrEqualTo(
                DateTimeOffset.UtcNow);
        }

        healthy = false;
        await monitor.ProbeOnceAsync(TestContext.Current.CancellationToken);
        using (var response = await SendAuthorizedAsync(client, _factory.Services, HttpMethod.Get,
            $"/api/plugins/installations/{workspaceId}", workspaceId, "plugin:installations:read"))
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
            var installation = document.RootElement.EnumerateArray().Single();
            installation.GetProperty("runtimeConnected").GetBoolean().ShouldBeTrue();
            installation.GetProperty("probeCondition").GetString().ShouldBe("blocked");
            installation.GetProperty("probeReasonCode").GetString().ShouldBe("peer_unavailable");
        }

        healthy = true;
        await monitor.ProbeOnceAsync(TestContext.Current.CancellationToken);
        using var recovered = await SendAuthorizedAsync(client, _factory.Services, HttpMethod.Get,
            $"/api/plugins/installations/{workspaceId}", workspaceId, "plugin:installations:read");
        using var recoveredDocument = JsonDocument.Parse(await recovered.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));
        recoveredDocument.RootElement.EnumerateArray().Single().GetProperty("probeCondition")
            .GetString().ShouldBe("responding");
    }
}
