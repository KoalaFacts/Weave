using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.RuntimeRecovery;

namespace Weave.Silo.Tests.RuntimeRecovery;

public sealed class WorkspaceRuntimeReadinessEndpointTests
{
    [Fact]
    public async Task ObserveAsync_InProcessWorkspace_UsesSameReadinessThroughHttpAndGrain()
    {
        await using var parent = new SiloFactory();
        await using var host = parent.WithWebHostBuilder(builder => builder.UseSetting("Weave:Auth:Mode", "none"));
        using var client = host.CreateClient();
        var id = $"readiness-{Guid.NewGuid():N}";
        var workspace = host.Services.GetRequiredService<IVirtualActorProvider>()
            .GetActor<IWorkspaceActor>(VirtualActorId.From(id));
        await workspace.StartAsync(new WorkspaceManifest { Name = id, Version = "1.0" });
        var route = $"/api/workspaces/{id}/runtime";
        SiloFactory.Authorize(client, host.Services, id, "workspace:runtime:recover");
        using (var denied = await client.GetAsync(route, TestContext.Current.CancellationToken))
            denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        SiloFactory.Authorize(client, host.Services, id, WorkspaceRuntimeRecovery.ReadGrant);
        var token = host.Services.GetRequiredService<ICapabilityTokenService>().Mint(new CapabilityTokenRequest
        {
            WorkspaceId = id,
            IssuedTo = "readiness-probe",
            Grants = [WorkspaceRuntimeRecovery.ReadGrant],
            Lifetime = TimeSpan.FromMinutes(5)
        });

        using (var response = await client.GetAsync(route, TestContext.Current.CancellationToken))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var rpc = await workspace.ObserveRuntimeAsync(token, TestContext.Current.CancellationToken);
            rpc.Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.Ready);
            body.RootElement.GetProperty("readiness").GetProperty("condition").GetString().ShouldBe(rpc.Readiness.Condition.ToString());
            body.RootElement.GetProperty("readiness").GetProperty("reasons").GetArrayLength().ShouldBe(0);
            body.RootElement.GetProperty("network").GetProperty("condition").GetString().ShouldBe("NotRequired");
            body.RootElement.GetProperty("containers").GetArrayLength().ShouldBe(0);
        }
        await workspace.StopAsync();
        using var stopped = await client.GetAsync(route, TestContext.Current.CancellationToken);
        stopped.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var stoppedBody = JsonDocument.Parse(await stopped.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        stoppedBody.RootElement.GetProperty("readiness").GetProperty("condition").GetString().ShouldBe("NotReady");
        stoppedBody.RootElement.GetProperty("readiness").GetProperty("reasons").EnumerateArray()
            .Select(reason => reason.GetString()).ShouldContain("WorkspaceNotRunning");
        (await workspace.ObserveRuntimeAsync(token, TestContext.Current.CancellationToken)).Readiness.Condition
            .ShouldBe(WorkspaceRuntimeReadinessCondition.NotReady);
    }
}
