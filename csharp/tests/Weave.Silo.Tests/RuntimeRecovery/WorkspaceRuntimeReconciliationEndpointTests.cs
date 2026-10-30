using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Weave.Management;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.RuntimeRecovery;

namespace Weave.Silo.Tests.RuntimeRecovery;

public sealed class WorkspaceRuntimeReconciliationEndpointTests
{
    [Fact]
    public async Task ReconcileAsync_HostRestart_RestoresResourceOnlyReadinessAndPersistsEvidence()
    {
        var directory = Path.Join(Path.GetTempPath(), $"weave-reconciliation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var id = $"restart-{Guid.NewGuid():N}";
        var route = $"/api/workspaces/{id}/runtime/reconcile";
        var managementId = Guid.NewGuid().ToString("N");
        Guid firstInstance;
        Guid reconciledInstance;
        try
        {
            await using (var parent = new SiloFactory())
            await using (var first = Durable(parent, directory))
            {
                using var client = first.CreateClient();
                var workspace = Actor(first, id);
                await workspace.StartAsync(new WorkspaceManifest { Name = id, Version = "1.0" });
                firstInstance = (await workspace.GetStateAsync()).RuntimeInstanceId;
            }
            await using (var parent = new SiloFactory())
            await using (var second = Durable(parent, directory))
            {
                using var client = second.CreateClient();
                var workspace = Actor(second, id);
                (await workspace.GetStateAsync()).RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RequiresReconciliation);
                var tokens = second.Services.GetRequiredService<ICapabilityTokenService>();
                var token = tokens.Mint(new CapabilityTokenRequest
                {
                    WorkspaceId = id,
                    IssuedTo = "reconciliation-test",
                    Grants = [WorkspaceRuntimeRecovery.ReadGrant, WorkspaceRuntimeRecovery.ReconcileGrant],
                    Lifetime = TimeSpan.FromMinutes(5)
                });
                var before = await workspace.ObserveRuntimeAsync(token, TestContext.Current.CancellationToken);
                before.Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.NotReady);
                var request = new WorkspaceRuntimeReconciliationRequest { ExpectedResourceSetDigest = before.ResourceSetDigest };
                using (var anonymous = await client.PostAsJsonAsync(route, request, TestContext.Current.CancellationToken))
                    anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
                SiloFactory.Authorize(client, second.Services, "foreign", WorkspaceRuntimeRecovery.ReconcileGrant);
                using (var foreign = await client.PostAsJsonAsync(route, request, TestContext.Current.CancellationToken))
                    foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
                SiloFactory.Authorize(client, second.Services, id, WorkspaceRuntimeRecovery.ReadGrant, WorkspaceRuntimeRecovery.RecoverGrant);
                using (var denied = await client.PostAsJsonAsync(route, request, TestContext.Current.CancellationToken))
                    denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
                SiloFactory.Authorize(client, second.Services, id, WorkspaceRuntimeRecovery.ReconcileGrant);
                client.DefaultRequestHeaders.Add("X-Weave-Management-Id", managementId);
                var competingId = Guid.NewGuid().ToString("N");
                using var competingRequest = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(request) };
                competingRequest.Headers.Add("X-Weave-Management-Id", competingId);
                var firstSubmission = client.PostAsJsonAsync(route, request, TestContext.Current.CancellationToken);
                var competingSubmission = client.SendAsync(competingRequest, TestContext.Current.CancellationToken);
                using var firstResponse = await firstSubmission;
                using var competingResponse = await competingSubmission;
                var response = firstResponse.StatusCode is HttpStatusCode.OK ? firstResponse : competingResponse;
                var stale = firstResponse.StatusCode is HttpStatusCode.OK ? competingResponse : firstResponse;
                var staleId = firstResponse.StatusCode is HttpStatusCode.OK ? competingId : managementId;
                if (firstResponse.StatusCode is not HttpStatusCode.OK)
                    managementId = competingId;
                stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
                (await stale.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("PlanChanged");
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
                using (var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)))
                {
                    body.RootElement.GetProperty("outcome").GetString().ShouldBe("Confirmed");
                    body.RootElement.GetProperty("observation").GetProperty("readiness").GetProperty("condition").GetString().ShouldBe("Ready");
                    body.RootElement.GetProperty("observation").GetProperty("startedOnCurrentHost").GetBoolean().ShouldBeFalse();
                }
                var after = await workspace.ObserveRuntimeAsync(token, TestContext.Current.CancellationToken);
                after.Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.Ready);
                after.ConfirmedOnCurrentHost.ShouldBeTrue();
                reconciledInstance = (await workspace.GetStateAsync()).RuntimeInstanceId;
                reconciledInstance.ShouldNotBe(firstInstance);
                var journal = second.Services.GetRequiredService<IManagementOperationJournal>();
                var recorded = journal.Find(managementId, TestContext.Current.CancellationToken).ShouldNotBeNull();
                journal.Find(staleId, TestContext.Current.CancellationToken)?.Outcome.ShouldBe(ManagementOperationOutcome.Failed);
                recorded.Outcome.ShouldBe(ManagementOperationOutcome.Succeeded);
                recorded.RequestDigest.ShouldBe(before.ResourceSetDigest);
                recorded.Action.ShouldBe(WorkspaceRuntimeRecovery.ReconcileGrant);
                (await workspace.ReconcileRuntimeAsync(request, token, managementId.ToUpperInvariant(),
                    TestContext.Current.CancellationToken)).Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.AlreadyAdmitted);
                (await workspace.ReconcileRuntimeAsync(request, token, Guid.NewGuid().ToString("N"),
                    TestContext.Current.CancellationToken)).Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.PlanChanged);
            }
            await using (var parent = new SiloFactory())
            await using (var third = Durable(parent, directory))
            {
                using var client = third.CreateClient();
                var state = await Actor(third, id).GetStateAsync();
                state.RuntimeInstanceId.ShouldBe(reconciledInstance);
                state.RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RequiresReconciliation);
                third.Services.GetRequiredService<IManagementOperationJournal>().Find(managementId,
                    TestContext.Current.CancellationToken)?.Outcome.ShouldBe(ManagementOperationOutcome.Succeeded);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Durable(SiloFactory parent, string directory) =>
        parent.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Weave:ActorStorage:Provider", "sqlite");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={Path.Join(directory, "actors.db")};Pooling=False");
            builder.UseSetting("Weave:Invocations:DatabasePath", Path.Join(directory, "invocations.db"));
        });

    private static IWorkspaceActor Actor(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, string id) =>
        host.Services.GetRequiredService<IVirtualActorProvider>().GetActor<IWorkspaceActor>(VirtualActorId.From(id));

    [Fact]
    public async Task ReconcileAsync_StaleResourceSet_ReturnsConflictWithoutChangingOwnership()
    {
        await using var host = new SiloFactory();
        using var client = host.CreateClient();
        var id = $"reconcile-{Guid.NewGuid():N}";
        var workspace = host.Services.GetRequiredService<IVirtualActorProvider>()
            .GetActor<IWorkspaceActor>(VirtualActorId.From(id));
        await workspace.StartAsync(new WorkspaceManifest { Name = id, Version = "1.0" });
        SiloFactory.Authorize(client, host.Services, id, "workspace:runtime:reconcile");
        using var response = await client.PostAsJsonAsync($"/api/workspaces/{id}/runtime/reconcile",
            new { expectedResourceSetDigest = new string('a', 64) }, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("PlanChanged");
        (await workspace.GetStateAsync()).RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.StartedOnThisHost);
    }
}
