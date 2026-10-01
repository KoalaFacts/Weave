using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Weave.Management;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Silo.Plugins;
using Weave.Tools.Tool;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.RuntimeRecovery;

namespace Weave.Silo.Tests.Plugins;

public sealed partial class McpWorkspacePluginFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcileRuntimeAsync_RestartedMcpTools_RestoresConnectionsWithoutInvoking(bool modernOnly)
    {
        var directory = Path.Join(Path.GetTempPath(), $"weave-mcp-restoration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var peer = new McpPeer("restoration", modernOnly);
            await peer.StartAsync();
            string workspaceId;
            string managementId;
            Guid confirmedInstance;
            await using (var first = new DurableSiloFactory(directory))
            {
                using var client = first.CreateClient();
                using var response = await SendStartAsync(client, CreateManifest(peer.Endpoint) with { Agents = [] },
                    Mint(first.Services, "silo", "workspace:create", secondGrant: "plugin:mcp_tools:install"));
                response.StatusCode.ShouldBe(HttpStatusCode.Created);
                using var state = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                workspaceId = state.RootElement.GetProperty("workspaceId").GetString()!;
            }
            await using (var second = new DurableSiloFactory(directory))
            {
                using var restarted = second.CreateClient();
                await second.Services.GetRequiredService<ToolInstallationRestorer>().Completion
                    .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                var actors = second.Services.GetRequiredService<IVirtualActorProvider>();
                var tool = actors.GetActor<IToolActor>(VirtualActorId.From($"{workspaceId}/echo"));
                (await tool.GetHandleAsync()).ShouldBeNull();
                var token = second.Services.GetRequiredService<ICapabilityTokenService>().Mint(new()
                {
                    WorkspaceId = workspaceId,
                    IssuedTo = "restoration-operator",
                    Grants = [WorkspaceRuntimeRecovery.ReconcileGrant, $"plugin:invoke:{workspaceId}/echo_server", "tool:echo:connect"],
                    Lifetime = TimeSpan.FromMinutes(5)
                });
                using var observedResponse = await SendPluginAsync(restarted, second.Services, HttpMethod.Get,
                    $"/api/workspaces/{workspaceId}/runtime", workspaceId, WorkspaceRuntimeRecovery.ReadGrant);
                observedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
                var observed = await observedResponse.Content.ReadFromJsonAsync<WorkspaceRuntimeSnapshot>(JsonOptions,
                    TestContext.Current.CancellationToken);
                observed!.HostedServicePlan!.BlockReason.ShouldBeNull();
                observed.HostedServicePlan.McpInstallations.Single().Url.ShouldBe(peer.Endpoint);
                observed.HostedServicePlan.McpInstallations.Single().Operation.ShouldBe("echo");
                var plan = new WorkspaceRuntimeReconciliationRequest
                {
                    ExpectedResourceSetDigest = observed.ResourceSetDigest,
                    ExpectedHostedServiceDigest = observed.HostedServicePlan.Digest
                };
                foreach (var missingGrant in new[] { "tool:echo:connect", $"plugin:invoke:{workspaceId}/echo_server" })
                {
                    var deniedToken = second.Services.GetRequiredService<ICapabilityTokenService>().Mint(new()
                    {
                        WorkspaceId = workspaceId,
                        IssuedTo = "denied-restoration",
                        Grants = token.Grants.Where(grant => grant != missingGrant).ToHashSet(StringComparer.Ordinal),
                        Lifetime = TimeSpan.FromMinutes(5)
                    });
                    using var denied = await SendRestorationAsync(restarted, workspaceId, plan, deniedToken,
                        Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);
                    denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
                    (await tool.GetHandleAsync()).ShouldBeNull();
                }
                using (var stale = await SendRestorationAsync(restarted, workspaceId,
                    plan with { ExpectedHostedServiceDigest = new string('a', 64) }, token, Guid.NewGuid().ToString("N"),
                    TestContext.Current.CancellationToken))
                {
                    stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
                    var changed = await stale.Content.ReadFromJsonAsync<WorkspaceRuntimeReconciliationResult>(JsonOptions,
                        TestContext.Current.CancellationToken);
                    changed!.Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.PlanChanged);
                    (await tool.GetHandleAsync()).ShouldBeNull();
                }
                peer.SchemaDescription = "Changed contract after host restoration.";
                using (var rejected = await SendRestorationAsync(restarted, workspaceId, plan, token,
                    Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken))
                {
                    rejected.StatusCode.ShouldBe(HttpStatusCode.Conflict);
                    var failure = await rejected.Content.ReadFromJsonAsync<WorkspaceRuntimeReconciliationResult>(JsonOptions,
                        TestContext.Current.CancellationToken);
                    failure!.Reason.ShouldBe("mcp-contract-rejected");
                    (await actors.GetActor<IWorkspaceActor>(VirtualActorId.From(workspaceId)).GetStateAsync())
                        .RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RequiresReconciliation);
                }
                peer.SchemaDescription = "Return text unchanged.";
                managementId = Guid.NewGuid().ToString("N");
                using var result = await SendRestorationAsync(restarted, workspaceId, plan, token, managementId,
                    TestContext.Current.CancellationToken);
                var body = await result.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                result.StatusCode.ShouldBe(HttpStatusCode.OK, body);
                var confirmation = await result.Content.ReadFromJsonAsync<WorkspaceRuntimeReconciliationResult>(JsonOptions,
                    TestContext.Current.CancellationToken);
                confirmation!.Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.Confirmed);
                confirmation.HostedServicesRestored.ShouldBeTrue();
                confirmation.Observation!.Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.Ready);
                confirmation.Observation.HostedServiceObservation!.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.Ready);
                confirmation.Observation.HostedServiceObservation.McpInstallations.Single().Reason.ShouldBeNull();
                var confirmedState = await actors.GetActor<IWorkspaceActor>(VirtualActorId.From(workspaceId)).GetStateAsync();
                confirmedState.RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RuntimeReconciledOnThisHost);
                confirmedInstance = confirmedState.RuntimeInstanceId;
                var restoredHandle = await tool.GetHandleAsync();
                restoredHandle!.IsConnected.ShouldBeTrue();
                peer.CallCount.ShouldBe(0);
                var record = second.Services.GetRequiredService<IManagementOperationJournal>().Find(managementId,
                    TestContext.Current.CancellationToken);
                record!.Outcome.ShouldBe(ManagementOperationOutcome.Succeeded);
                record.AuthorizedGrants.ShouldContain("tool:echo:connect");
                record.AuthorizedGrants.ShouldContain($"plugin:invoke:{workspaceId}/echo_server");
                using var duplicate = await SendRestorationAsync(restarted, workspaceId, plan, token, managementId,
                    TestContext.Current.CancellationToken);
                duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
                var duplicateResult = await duplicate.Content.ReadFromJsonAsync<WorkspaceRuntimeReconciliationResult>(JsonOptions,
                    TestContext.Current.CancellationToken);
                duplicateResult!.Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.AlreadyAdmitted);
                (await tool.GetHandleAsync())!.ConnectionId.ShouldBe(restoredHandle.ConnectionId);
                peer.CallCount.ShouldBe(0);

                // Confirmation remains historical evidence when a current connection later fails.
                await tool.DisconnectAsync();
                using var disconnectedResponse = await SendPluginAsync(restarted, second.Services, HttpMethod.Get,
                    $"/api/workspaces/{workspaceId}/runtime", workspaceId, WorkspaceRuntimeRecovery.ReadGrant);
                disconnectedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
                var disconnected = (await disconnectedResponse.Content.ReadFromJsonAsync<WorkspaceRuntimeSnapshot>(JsonOptions,
                    TestContext.Current.CancellationToken))!;
                disconnected.Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.NotReady);
                disconnected.Readiness.Reasons.Select(reason => reason.ToString()).ShouldContain("HostedServicesNotReady");
                disconnected.HostedServiceObservation!.McpInstallations.Single().Reason.ShouldBe("mcp-tool-not-connected");
                disconnected.ConfirmedOnCurrentHost.ShouldBeTrue();
                disconnected.ResourceSetDigest.ShouldBe(confirmation.Observation.ResourceSetDigest);
                var readToken = second.Services.GetRequiredService<ICapabilityTokenService>().Mint(new()
                {
                    WorkspaceId = workspaceId,
                    IssuedTo = "readiness-operator",
                    Grants = [WorkspaceRuntimeRecovery.ReadGrant],
                    Lifetime = TimeSpan.FromMinutes(5)
                });
                var workspace = actors.GetActor<IWorkspaceActor>(VirtualActorId.From(workspaceId));
                var rpc = await workspace.ObserveRuntimeAsync(readToken, TestContext.Current.CancellationToken);
                rpc.Readiness.Condition.ShouldBe(disconnected.Readiness.Condition);
                rpc.Readiness.Reasons.ShouldBe(disconnected.Readiness.Reasons);
                (await workspace.GetStateAsync()).RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RuntimeReconciledOnThisHost);
                second.Services.GetRequiredService<IManagementOperationJournal>().Find(managementId,
                    TestContext.Current.CancellationToken).ShouldBe(record);
                (await tool.GetHandleAsync()).ShouldBeNull();
                peer.CallCount.ShouldBe(0);
            }
            await using var third = new DurableSiloFactory(directory);
            using var thirdClient = third.CreateClient();
            var restoredState = await third.Services.GetRequiredService<IVirtualActorProvider>()
                .GetActor<IWorkspaceActor>(VirtualActorId.From(workspaceId)).GetStateAsync();
            restoredState.RuntimeInstanceId.ShouldBe(confirmedInstance);
            restoredState.RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RequiresReconciliation);
            third.Services.GetRequiredService<IManagementOperationJournal>().Find(managementId,
                TestContext.Current.CancellationToken)!.Outcome.ShouldBe(ManagementOperationOutcome.Succeeded);
            peer.CallCount.ShouldBe(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<HttpResponseMessage> SendRestorationAsync(HttpClient client, string workspaceId,
        WorkspaceRuntimeReconciliationRequest plan, CapabilityToken token, string id, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/runtime/reconcile")
        {
            Content = JsonContent.Create(plan)
        };
        request.Headers.Add("X-Weave-Capability", Encode(token));
        request.Headers.Add("X-Weave-Management-Id", id);
        return await client.SendAsync(request, ct);
    }
}
