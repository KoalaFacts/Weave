using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Weave.Silo.Plugins;
using Weave.Tools.InstallMcpTool;

namespace Weave.Silo.Tests.Plugins;

public sealed partial class McpWorkspacePluginFlowTests
{
    [Fact]
    public async Task GetInstallationInventory_RequiresWorkspaceReadGrant()
    {
        var directory = Path.Join(Path.GetTempPath(), $"weave-mcp-inventory-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var host = new DurableSiloFactory(directory);
            using var client = host.CreateClient();
            var workspaceId = $"ws_unknown_{Guid.NewGuid():N}";
            var path = $"/api/plugins/installations/{workspaceId}";

            using (var anonymous = await client.GetAsync(path, TestContext.Current.CancellationToken))
                anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            using (var wrongWorkspace = await SendPluginAsync(client, host.Services, HttpMethod.Get,
                path, "other", "plugin:installations:read"))
                wrongWorkspace.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            using (var missingGrant = await SendPluginAsync(client, host.Services, HttpMethod.Get,
                path, workspaceId, "plugin:mcp_tools:enable"))
                missingGrant.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            using (var authorized = await SendPluginAsync(client, host.Services, HttpMethod.Get,
                path, workspaceId, "plugin:installations:read"))
                authorized.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GetInstallationInventory_TwoWorkspaces_ListsOnlyRequestedInstallationWithoutConfiguration()
    {
        var directory = Path.Join(Path.GetTempPath(), $"weave-mcp-inventory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var peer = new McpPeer("inventory");
            await using var otherPeer = new McpPeer("other-inventory");
            await peer.StartAsync();
            await otherPeer.StartAsync();
            await using var host = new DurableSiloFactory(directory);
            using var client = host.CreateClient();
            var workspaceId = await StartWorkspaceAsync(client, host.Services, peer);
            var otherWorkspaceId = await StartWorkspaceAsync(client, host.Services, otherPeer);
            var path = $"/api/plugins/installations/{workspaceId}";

            using var allowed = await SendPluginAsync(client, host.Services, HttpMethod.Get,
                path, workspaceId, "plugin:installations:read");
            allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await allowed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(body);
            var installation = document.RootElement.EnumerateArray().Single();
            installation.GetProperty("id").GetString().ShouldBe($"{workspaceId}/echo_server");
            installation.GetProperty("pluginName").GetString().ShouldBe("echo_server");
            installation.GetProperty("type").GetString().ShouldBe("mcp_tools");
            installation.GetProperty("desiredEnabled").GetBoolean().ShouldBeTrue();
            installation.GetProperty("runtimeConnected").GetBoolean().ShouldBeTrue();
            installation.GetProperty("condition").GetString().ShouldBe("ready");
            installation.GetProperty("reasonCode").ValueKind.ShouldBe(JsonValueKind.Null);
            installation.GetProperty("lastCheckedAt").GetDateTimeOffset()
                .ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow);
            body.ShouldNotContain(peer.Endpoint);
            body.ShouldNotContain(otherWorkspaceId);
            body.ShouldNotContain("configDigest");
            body.ShouldNotContain("contractDigest");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GetInstallationInventory_AfterStopAndRestart_ShowsDisabledRecord()
    {
        var directory = Path.Join(Path.GetTempPath(), $"weave-mcp-inventory-restart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var peer = new McpPeer("inventory-restart");
            await peer.StartAsync();
            string workspaceId;
            await using (var first = new DurableSiloFactory(directory))
            {
                using var client = first.CreateClient();
                workspaceId = await StartWorkspaceAsync(client, first.Services, peer);
                using var stop = await SendPluginAsync(client, first.Services, HttpMethod.Delete,
                    $"/api/workspaces/{workspaceId}", workspaceId, "workspace:stop",
                    token: Mint(first.Services, workspaceId, "workspace:stop", secondGrant: "plugin:mcp_tools:disable"));
                stop.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            }

            await using (var second = new DurableSiloFactory(directory))
            {
                using var client = second.CreateClient();
                await second.Services.GetRequiredService<ToolInstallationRestorer>().Completion
                    .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                using var response = await SendPluginAsync(client, second.Services, HttpMethod.Get,
                    $"/api/plugins/installations/{workspaceId}", workspaceId, "plugin:installations:read");
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(
                    TestContext.Current.CancellationToken));
                var installation = document.RootElement.EnumerateArray().Single();
                installation.GetProperty("id").GetString().ShouldBe($"{workspaceId}/echo_server");
                installation.GetProperty("definitionRevision").GetString()
                    .ShouldBe(McpToolInstallation.ImplementationRevision);
                installation.GetProperty("requestedPermissions").GetArrayLength().ShouldBe(0);
                installation.GetProperty("grantedPermissions").GetArrayLength().ShouldBe(0);
                installation.GetProperty("hasCredentialReferences").GetBoolean().ShouldBeFalse();
                installation.GetProperty("desiredEnabled").GetBoolean().ShouldBeFalse();
                installation.GetProperty("runtimeConnected").GetBoolean().ShouldBeFalse();
                installation.GetProperty("condition").GetString().ShouldBe("disabled");
                installation.GetProperty("reasonCode").ValueKind.ShouldBe(JsonValueKind.Null);
                installation.GetProperty("lastCheckedAt").ValueKind.ShouldBe(JsonValueKind.Null);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GetInstallationInventory_RestartWithUnavailableMcpPeer_ReportsSafeFailure()
    {
        var directory = Path.Join(Path.GetTempPath(), $"weave-mcp-inventory-unavailable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string workspaceId;
            string endpoint;
            await using (var peer = new McpPeer("recovery-unavailable"))
            {
                await peer.StartAsync();
                endpoint = peer.Endpoint;
                await using var first = new DurableSiloFactory(directory);
                using var client = first.CreateClient();
                workspaceId = await StartWorkspaceAsync(client, first.Services, peer);
            }

            await using var second = new DurableSiloFactory(directory);
            using var restoredClient = second.CreateClient();
            await second.Services.GetRequiredService<ToolInstallationRestorer>().Completion
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            using var response = await SendPluginAsync(restoredClient, second.Services, HttpMethod.Get,
                $"/api/plugins/installations/{workspaceId}", workspaceId, "plugin:installations:read");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(body);
            var installation = document.RootElement.EnumerateArray().Single();
            installation.GetProperty("desiredEnabled").GetBoolean().ShouldBeTrue();
            installation.GetProperty("runtimeConnected").GetBoolean().ShouldBeFalse();
            installation.GetProperty("condition").GetString().ShouldBe("blocked");
            installation.GetProperty("reasonCode").GetString().ShouldBe("peer_unavailable");
            installation.GetProperty("lastCheckedAt").GetDateTimeOffset()
                .ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow);
            body.ShouldNotContain(endpoint);
            body.ShouldNotContain("error");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GetInstallationInventory_RestartWithChangedMcpContract_ReportsSafeFailure()
    {
        var directory = Path.Join(Path.GetTempPath(), $"weave-mcp-inventory-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var peer = new McpPeer("recovery-contract");
            await peer.StartAsync();
            string workspaceId;
            await using (var first = new DurableSiloFactory(directory))
            {
                using var client = first.CreateClient();
                workspaceId = await StartWorkspaceAsync(client, first.Services, peer);
            }
            peer.SchemaDescription = "changed contract";

            await using var second = new DurableSiloFactory(directory);
            using var restoredClient = second.CreateClient();
            await second.Services.GetRequiredService<ToolInstallationRestorer>().Completion
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            using var response = await SendPluginAsync(restoredClient, second.Services, HttpMethod.Get,
                $"/api/plugins/installations/{workspaceId}", workspaceId, "plugin:installations:read");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(body);
            var installation = document.RootElement.EnumerateArray().Single();
            installation.GetProperty("condition").GetString().ShouldBe("blocked");
            installation.GetProperty("reasonCode").GetString().ShouldBe("contract_rejected");
            body.ShouldNotContain(peer.Endpoint);
            body.ShouldNotContain("changed contract");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GetInstallationInventory_FailedManualEnable_RetainsSafeLastAttempt()
    {
        var directory = Path.Join(Path.GetTempPath(), $"weave-mcp-inventory-enable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var host = new DurableSiloFactory(directory);
            using var client = host.CreateClient();
            string workspaceId;
            await using (var peer = new McpPeer("manual-enable"))
            {
                await peer.StartAsync();
                workspaceId = await StartWorkspaceAsync(client, host.Services, peer);
                using var disable = await SendPluginAsync(client, host.Services, HttpMethod.Delete,
                    $"/api/plugins/{workspaceId}/echo_server", workspaceId, "plugin:mcp_tools:disable");
                disable.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            }

            using var enable = await SendPluginAsync(client, host.Services, HttpMethod.Post,
                "/api/plugins", workspaceId, "plugin:mcp_tools:enable", new
                {
                    Name = $"{workspaceId}/echo_server",
                    Type = "mcp_tools"
                });
            enable.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            using var response = await SendPluginAsync(client, host.Services, HttpMethod.Get,
                $"/api/plugins/installations/{workspaceId}", workspaceId, "plugin:installations:read");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
            var installation = document.RootElement.EnumerateArray().Single();
            installation.GetProperty("desiredEnabled").GetBoolean().ShouldBeFalse();
            installation.GetProperty("runtimeConnected").GetBoolean().ShouldBeFalse();
            installation.GetProperty("condition").GetString().ShouldBe("disabled");
            installation.GetProperty("reasonCode").GetString().ShouldBe("peer_unavailable");
            installation.GetProperty("lastCheckedAt").GetDateTimeOffset()
                .ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
