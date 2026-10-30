using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Mvc.Testing;
using Weave.Management;
using Weave.Shared.Ids;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Manifest;

namespace Weave.Silo.Tests;

[Trait("Category", "Integration")]
public sealed class WorkspaceManagementConflictHttpTests(SiloFactory factory) : IClassFixture<SiloFactory>
{
    private const string IdHeader = "X-Weave-Management-Id";

    [Fact]
    public async Task Start_RuntimeIdentityConflict_RetainsAdmittedIdAndUnknownOutcomeWithoutReplay()
    {
        using var files = new PersistedWorkspaceReadFixture();
        await using var host = files.CreateHost(factory);
        using var client = host.CreateClient();
        using var sidecar = new TcpListener(IPAddress.Loopback, 0);
        sidecar.Start();
        var port = ((IPEndPoint)sidecar.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
        var manifest = new WorkspaceManifest
        {
            Name = "case-conflict",
            Version = "1.0",
            Plugins = new Dictionary<string, PluginDefinition>
            {
                ["sidecar"] = new() { Type = "dapr_tools", Config = new() { ["port"] = port } },
                ["Sidecar"] = new() { Type = "dapr_tools", Config = new() { ["port"] = port } }
            },
            Tools = new Dictionary<string, ToolDefinition>
            {
                ["first"] = new() { Type = "dapr", RequiresPlugin = "sidecar", Dapr = new() { AppId = "fixture" } },
                ["second"] = new() { Type = "dapr", RequiresPlugin = "Sidecar", Dapr = new() { AppId = "fixture" } }
            }
        };
        new ManifestParser().Validate(manifest).ShouldBeEmpty();
        var id = Guid.NewGuid().ToString("N");
        SiloFactory.Authorize(client, host.Services, "silo", "workspace:create", "plugin:dapr_tools:install");
        using var request = StartRequest(manifest, id);

        using var conflict = await client.SendAsync(request, TestContext.Current.CancellationToken);

        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await conflict.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("Dapr plugin names differ only in case within this manifest.");
        conflict.Headers.GetValues(IdHeader).ShouldHaveSingleItem().ShouldBe(id);
        sidecar.Pending().ShouldBeFalse("the runtime identity guard must reject before any sidecar connection");
        var admitted = await ReadOperationAsync(client, host, "silo", id);
        admitted.Action.ShouldBe("workspace:create");
        admitted.Outcome.ShouldBe(ManagementOperationOutcome.OutcomeUnknown);
        admitted.CompletedAt.ShouldBeNull();
        admitted.Target.ShouldNotBeNullOrWhiteSpace();
        File.Exists(Path.Join(files.Root, "management.db")).ShouldBeTrue();

        SiloFactory.Authorize(client, host.Services, "silo", "workspace:create");
        using var repeatedRequest = StartRequest(new WorkspaceManifest { Name = "must-not-replay", Version = "1.0" }, id);
        using var repeated = await client.SendAsync(repeatedRequest, TestContext.Current.CancellationToken);
        repeated.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await repeated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("management-operation-already-admitted");
        (await ReadOperationAsync(client, host, "silo", id)).ShouldBe(admitted);
        using var workspaces = await client.GetAsync("/api/workspaces", TestContext.Current.CancellationToken);
        workspaces.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await workspaces.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain("must-not-replay");
    }

    [Fact]
    public async Task Stop_CreatingRuntimeMismatch_RetainsAdmittedIdUnknownOutcomeAndWorkspaceAcrossRestart()
    {
        using var files = new PersistedWorkspaceReadFixture();
        var workspaceId = WorkspaceId.From($"retained-{Guid.NewGuid():N}");
        await using (var seedHost = files.CreateHost(factory))
        {
            var bootstrapId = $"bootstrap-{Guid.NewGuid():N}";
            var bootstrap = PersistedWorkspaceReadFixture.Actor(seedHost, bootstrapId);
            await bootstrap.StartAsync(new WorkspaceManifest { Name = "bootstrap", Version = "1.0" });
            await bootstrap.StopAsync();
            var stateName = await files.ReadStateNameAsync(seedHost, bootstrapId);
            await PersistedWorkspaceReadFixture.SeedAsync(seedHost, stateName, new WorkspaceState
            {
                WorkspaceId = workspaceId,
                Name = "retained-other-runtime",
                Status = WorkspaceStatus.Running,
                RuntimeName = "fixture-other-runtime",
                RuntimeInstanceId = Guid.NewGuid(),
                NetworkId = NetworkId.From("retained-owned-network")
            });
        }

        var id = Guid.NewGuid().ToString("N");
        ManagementOperationRecord admitted;
        byte[] retained;
        await using (var host = files.CreateHost(factory))
        {
            using var client = host.CreateClient();
            var state = await PersistedWorkspaceReadFixture.Actor(host, workspaceId.ToString()).GetStateAsync();
            state.Status.ShouldBe(WorkspaceStatus.Running);
            state.RuntimeName.ShouldBe("fixture-other-runtime");
            retained = await files.ReadPayloadAsync(workspaceId.ToString());
            SiloFactory.Authorize(client, host.Services, workspaceId.ToString(), "workspace:stop");
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/workspaces/{workspaceId}");
            request.Headers.Add(IdHeader, id);

            using var conflict = await client.SendAsync(request, TestContext.Current.CancellationToken);

            conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await conflict.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
                .ShouldContain("creating runtime does not match this Host");
            conflict.Headers.GetValues(IdHeader).ShouldHaveSingleItem().ShouldBe(id);
            admitted = await ReadOperationAsync(client, host, workspaceId.ToString(), id);
            admitted.Action.ShouldBe("workspace:stop");
            admitted.Target.ShouldBe(workspaceId.ToString());
            admitted.Outcome.ShouldBe(ManagementOperationOutcome.OutcomeUnknown);
            admitted.CompletedAt.ShouldBeNull();
            (await files.ReadPayloadAsync(workspaceId.ToString())).ShouldBe(retained);
        }

        await using (var restarted = files.CreateHost(factory))
        {
            using var client = restarted.CreateClient();
            (await ReadOperationAsync(client, restarted, workspaceId.ToString(), id)).ShouldBe(admitted);
            SiloFactory.Authorize(client, restarted.Services, workspaceId.ToString(), "workspace:stop");
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/workspaces/{workspaceId}");
            request.Headers.Add(IdHeader, id);
            using var repeated = await client.SendAsync(request, TestContext.Current.CancellationToken);
            repeated.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await repeated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
                .ShouldContain("management-operation-already-admitted");
            (await ReadOperationAsync(client, restarted, workspaceId.ToString(), id)).ShouldBe(admitted);
            (await files.ReadPayloadAsync(workspaceId.ToString())).ShouldBe(retained);
        }
    }

    private static HttpRequestMessage StartRequest(WorkspaceManifest manifest, string id)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/workspaces")
        {
            Content = JsonContent.Create(new { Manifest = manifest })
        };
        request.Headers.Add(IdHeader, id);
        return request;
    }

    private static async Task<ManagementOperationRecord> ReadOperationAsync(HttpClient client,
        WebApplicationFactory<Program> host, string workspace, string id)
    {
        SiloFactory.Authorize(client, host.Services, workspace, "management:operations:read");
        using var response = await client.GetAsync($"/api/management/operations/{workspace}/{id}", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var operation = (await response.Content.ReadFromJsonAsync<ManagementOperationRecord>(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        operation.Id.ShouldBe(id);
        operation.WorkspaceId.ShouldBe(workspace);
        return operation;
    }
}
