using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weave.Management;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Silo.Plugins;
using Weave.Silo.RuntimeRecovery;
using Weave.Tools.Tool;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.RuntimeRecovery;

namespace Weave.Silo.Tests.Plugins;

public sealed partial class McpWorkspacePluginFlowTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ReconcileRuntimeAsync_ServiceFailsAfterRestoration_BlocksUntilFreshRecovery(bool modernOnly, bool disconnect)
    {
        var directory = Path.Join(Path.GetTempPath(), $"weave-mcp-verification-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var peer = new McpPeer("verification", modernOnly);
            await peer.StartAsync();
            string workspaceId;
            await using (var first = new DurableSiloFactory(directory))
            {
                using var client = first.CreateClient();
                using var created = await SendStartAsync(client, CreateManifest(peer.Endpoint) with { Agents = [] },
                    Mint(first.Services, "silo", "workspace:create", secondGrant: "plugin:mcp_tools:install"));
                created.StatusCode.ShouldBe(HttpStatusCode.Created);
                using var state = JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                workspaceId = state.RootElement.GetProperty("workspaceId").GetString()!;
            }
            var failedId = Guid.NewGuid().ToString("N");
            var restorations = 0;
            var injectFailure = true;
            IToolActor? tool = null;
            await using (var second = new DurableSiloFactory(directory, configureServices: services =>
            {
                services.RemoveAll<IWorkspaceHostedServiceRecovery>();
                services.AddSingleton<IWorkspaceHostedServiceRecovery>(provider => new RecoveryVerificationBoundary(
                    ActivatorUtilities.CreateInstance<McpWorkspaceServiceRecovery>(provider), async () =>
                    {
                        restorations++;
                        if (!injectFailure)
                            return;
                        if (disconnect)
                            await tool!.DisconnectAsync();
                        else
                            peer.SchemaDescription = "Changed after successful restoration.";
                    }));
            }))
            {
                using var client = second.CreateClient();
                await second.Services.GetRequiredService<ToolInstallationRestorer>().Completion
                    .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                var actors = second.Services.GetRequiredService<IVirtualActorProvider>();
                tool = actors.GetActor<IToolActor>(VirtualActorId.From($"{workspaceId}/echo"));
                var token = second.Services.GetRequiredService<ICapabilityTokenService>().Mint(new()
                {
                    WorkspaceId = workspaceId,
                    IssuedTo = "verification-operator",
                    Grants = [WorkspaceRuntimeRecovery.ReconcileGrant, $"plugin:invoke:{workspaceId}/echo_server", "tool:echo:connect"],
                    Lifetime = TimeSpan.FromMinutes(5)
                });
                using var read = await SendPluginAsync(client, second.Services, HttpMethod.Get,
                    $"/api/workspaces/{workspaceId}/runtime", workspaceId, WorkspaceRuntimeRecovery.ReadGrant);
                var observed = (await read.Content.ReadFromJsonAsync<WorkspaceRuntimeSnapshot>(JsonOptions,
                    TestContext.Current.CancellationToken))!;
                var plan = new WorkspaceRuntimeReconciliationRequest
                {
                    ExpectedResourceSetDigest = observed.ResourceSetDigest,
                    ExpectedHostedServiceDigest = observed.HostedServicePlan!.Digest
                };
                using var failed = await SendRestorationAsync(client, workspaceId, plan, token, failedId, TestContext.Current.CancellationToken);
                var failure = (await failed.Content.ReadFromJsonAsync<WorkspaceRuntimeReconciliationResult>(JsonOptions,
                    TestContext.Current.CancellationToken))!;
                failed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
                failure.Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.Blocked);
                failure.Reason.ShouldBe("hosted-services-not-ready");
                failure.HostedServicesRestored.ShouldBeFalse();
                failure.Observation!.HostedServiceObservation!.McpInstallations.Single().Reason
                    .ShouldBe(disconnect ? "mcp-tool-not-connected" : "mcp-contract-rejected");
                (await actors.GetActor<IWorkspaceActor>(VirtualActorId.From(workspaceId)).GetStateAsync())
                    .RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RequiresReconciliation);
                second.Services.GetRequiredService<IManagementOperationJournal>().Find(failedId,
                    TestContext.Current.CancellationToken)!.Outcome.ShouldBe(ManagementOperationOutcome.Failed);
                injectFailure = false;
                peer.SchemaDescription = "Return text unchanged.";
                using var duplicate = await SendRestorationAsync(client, workspaceId, plan, token, failedId, TestContext.Current.CancellationToken);
                var duplicateResult = (await duplicate.Content.ReadFromJsonAsync<WorkspaceRuntimeReconciliationResult>(JsonOptions,
                    TestContext.Current.CancellationToken))!;
                duplicateResult.Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.AlreadyAdmitted);
                restorations.ShouldBe(1);
                using var refreshed = await SendPluginAsync(client, second.Services, HttpMethod.Get,
                    $"/api/workspaces/{workspaceId}/runtime", workspaceId, WorkspaceRuntimeRecovery.ReadGrant);
                refreshed.StatusCode.ShouldBe(HttpStatusCode.OK);
                var current = (await refreshed.Content.ReadFromJsonAsync<WorkspaceRuntimeSnapshot>(JsonOptions,
                    TestContext.Current.CancellationToken))!;
                var freshPlan = new WorkspaceRuntimeReconciliationRequest
                {
                    ExpectedResourceSetDigest = current.ResourceSetDigest,
                    ExpectedHostedServiceDigest = current.HostedServicePlan!.Digest
                };
                using var recovered = await SendRestorationAsync(client, workspaceId, freshPlan, token,
                    Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);
                recovered.StatusCode.ShouldBe(HttpStatusCode.OK);
                var recovery = (await recovered.Content.ReadFromJsonAsync<WorkspaceRuntimeReconciliationResult>(JsonOptions,
                    TestContext.Current.CancellationToken))!;
                recovery.Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.Confirmed);
                recovery.HostedServicesRestored.ShouldBeTrue();
                recovery.Observation!.HostedServiceObservation!.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.Ready);
                restorations.ShouldBe(2);
                peer.CallCount.ShouldBe(0);
            }
            await using var third = new DurableSiloFactory(directory);
            using var thirdClient = third.CreateClient();
            third.Services.GetRequiredService<IManagementOperationJournal>().Find(failedId,
                TestContext.Current.CancellationToken)!.Outcome.ShouldBe(ManagementOperationOutcome.Failed);
            peer.CallCount.ShouldBe(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecoveryVerificationBoundary(IWorkspaceHostedServiceRecovery inner, Func<Task> afterRestoration)
        : IWorkspaceHostedServiceRecovery
    {
        public Task<WorkspaceHostedServicePlan> DescribeAsync(WorkspaceHostedServices services, CancellationToken ct)
            => inner.DescribeAsync(services, ct);
        public Task<WorkspaceHostedServiceObservation> ObserveAsync(WorkspaceHostedServices services, string expectedDigest, CancellationToken ct)
            => inner.ObserveAsync(services, expectedDigest, ct);
        public async Task<string?> RestoreAsync(WorkspaceHostedServices services, string expectedDigest, CapabilityToken token, CancellationToken ct)
        {
            var failure = await inner.RestoreAsync(services, expectedDigest, token, ct);
            if (failure is null)
                await afterRestoration();
            return failure;
        }
    }
}
