using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Weave.Shared.VirtualActors;
using Weave.Tools.Tool;
using Weave.Workspaces.RuntimeRecovery;

namespace Weave.Silo.Tests.Plugins;

public sealed partial class McpWorkspacePluginFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObserveRuntimeAsync_McpServices_ReturnsFreshConnectionDiagnostics(bool modernOnly)
    {
        var directory = Path.Join(Path.GetTempPath(), $"weave-mcp-observation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var peer = new McpPeer("observation", modernOnly);
            await peer.StartAsync();
            await using var host = new DurableSiloFactory(directory);
            using var client = host.CreateClient();
            using var created = await SendStartAsync(client, CreateManifest(peer.Endpoint) with { Agents = [] },
                Mint(host.Services, "silo", "workspace:create", secondGrant: "plugin:mcp_tools:install"));
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
            using var state = JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var workspaceId = state.RootElement.GetProperty("workspaceId").GetString()!;
            using (var denied = await client.GetAsync($"/api/workspaces/{workspaceId}/runtime", TestContext.Current.CancellationToken))
                denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            using (var wrongOwner = await SendPluginAsync(client, host.Services, HttpMethod.Get,
                $"/api/workspaces/{workspaceId}/runtime", "other", WorkspaceRuntimeRecovery.ReadGrant))
                wrongOwner.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            var tool = host.Services.GetRequiredService<IVirtualActorProvider>()
                .GetActor<IToolActor>(VirtualActorId.From($"{workspaceId}/echo"));
            var handle = await tool.GetHandleAsync();
            var ready = await ReadAsync();
            ready.HostedServiceObservation!.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.Ready);
            ready.HostedServiceObservation.McpInstallations.Single().InstallationId.ShouldBe($"{workspaceId}/echo_server");
            ready.HostedServiceObservation.McpInstallations.Single().Reason.ShouldBeNull();
            peer.SchemaDescription = "Changed remote contract.";
            var rejected = await ReadAsync();
            rejected.Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.Ready);
            rejected.HostedServiceObservation!.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.NotReady);
            rejected.HostedServiceObservation.McpInstallations.Single().Reason.ShouldBe("mcp-contract-rejected");
            rejected.HostedServicePlan!.Digest.ShouldBe(ready.HostedServicePlan!.Digest);
            peer.SchemaDescription = "Return text unchanged.";
            (await ReadAsync()).HostedServiceObservation!.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.Ready);
            (await tool.GetHandleAsync())!.ConnectionId.ShouldBe(handle!.ConnectionId);
            await tool.DisconnectAsync();
            var disconnected = await ReadAsync();
            disconnected.HostedServiceObservation!.McpInstallations.Single().Reason.ShouldBe("mcp-tool-not-connected");
            (await tool.GetHandleAsync()).ShouldBeNull();
            await peer.StopAsync();
            var unavailable = await ReadAsync();
            unavailable.HostedServiceObservation!.McpInstallations.Single().Reason.ShouldBe("mcp-peer-unavailable");
            unavailable.ConfirmedOnCurrentHost.ShouldBeTrue();
            peer.CallCount.ShouldBe(0);

            async Task<WorkspaceRuntimeSnapshot> ReadAsync()
            {
                using var response = await SendPluginAsync(client, host.Services, HttpMethod.Get,
                    $"/api/workspaces/{workspaceId}/runtime", workspaceId, WorkspaceRuntimeRecovery.ReadGrant);
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
                return (await response.Content.ReadFromJsonAsync<WorkspaceRuntimeSnapshot>(JsonOptions,
                    TestContext.Current.CancellationToken))!;
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
