using System.Net;
using System.Net.Http.Json;
using Weave.Shared.Ids;
using Weave.Silo.Plugins;
using Weave.Tools.InstallDaprTool;
using Weave.Tools.InstallMcpTool;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Manifest;

namespace Weave.Silo.Tests;

[Trait("Category", "Integration")]
public sealed class PersistedWorkspaceReadTests
{
    [Fact]
    public async Task Get_AfterRestart_IdOnlyStoppedRecordIsMissingButLifecycleEvidenceRemainsVisible()
    {
        using var fixture = new PersistedWorkspaceReadFixture();
        const string ghost = "old-id-only-activation";
        const string empty = "valid-empty-stopped";
        var retained = RetainedStates();
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using (var parent = new SiloFactory())
        await using (var host = fixture.CreateHost(parent))
        {
            using var client = host.CreateClient();
            // Obtain the real provider's state name and row identity through its actual actor path.
            var bootstrap = PersistedWorkspaceReadFixture.Actor(host, ghost);
            await bootstrap.StartAsync(new WorkspaceManifest { Name = ghost, Version = "1.0" });
            await bootstrap.StopAsync();
            var stateName = await fixture.ReadStateNameAsync(host, ghost);
            await PersistedWorkspaceReadFixture.SeedAsync(host, stateName,
                new WorkspaceState { WorkspaceId = WorkspaceId.From(ghost) });
            foreach (var state in retained)
                await PersistedWorkspaceReadFixture.SeedAsync(host, stateName, state);
            var valid = PersistedWorkspaceReadFixture.Actor(host, empty);
            await valid.StartAsync(new WorkspaceManifest { Name = empty, Version = "1.0" });
            await valid.StopAsync();
            var stopped = await valid.GetStateAsync();
            stopped.ActiveAgents.ShouldBeEmpty();
            stopped.ActiveTools.ShouldBeEmpty();
            stopped.ActivePlugins.ShouldBeEmpty();
            stopped.StartedAt.ShouldNotBeNull();
            stopped.StoppedAt.ShouldNotBeNull();
            foreach (var id in retained.Select(state => state.WorkspaceId.ToString()).Append(ghost).Append(empty))
                payloads[id] = await fixture.ReadPayloadAsync(id);
        }

        await using (var parent = new SiloFactory())
        await using (var host = fixture.CreateHost(parent))
        {
            using var client = host.CreateClient();
            foreach (var id in payloads.Keys)
            {
                SiloFactory.Authorize(client, host.Services, id, PluginInstallationAuthority.InstallationReadGrant);
                using var workspace = await client.GetAsync($"/api/workspaces/{id}", TestContext.Current.CancellationToken);
                using var plugins = await client.GetAsync($"/api/plugins/installations/{id}", TestContext.Current.CancellationToken);
                var expected = id == ghost ? HttpStatusCode.NotFound : HttpStatusCode.OK;
                workspace.StatusCode.ShouldBe(expected, id);
                plugins.StatusCode.ShouldBe(expected, id);
                if (id == ghost)
                {
                    (await workspace.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("not found");
                    (await plugins.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("not found");
                    // Repeat reads must neither promote the old activation nor remove its persisted evidence.
                    using var repeated = await client.GetAsync($"/api/workspaces/{id}", TestContext.Current.CancellationToken);
                    repeated.StatusCode.ShouldBe(HttpStatusCode.NotFound);
                }
                if (id == empty)
                {
                    var items = await plugins.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);
                    items.GetArrayLength().ShouldBe(0);
                    var state = await PersistedWorkspaceReadFixture.Actor(host, id).GetStateAsync();
                    state.Status.ShouldBe(WorkspaceStatus.Stopped);
                    state.Name.ShouldBe(empty);
                    state.StartedAt.ShouldNotBeNull();
                    state.StoppedAt.ShouldNotBeNull();
                }
                (await fixture.ReadPayloadAsync(id)).ShouldBe(payloads[id], $"GET must preserve persisted state for {id}.");
            }
        }
    }

    private static WorkspaceState[] RetainedStates() =>
    [
        new() { WorkspaceId = WorkspaceId.From("retained-stopping"), Status = WorkspaceStatus.Stopping },
        new() { WorkspaceId = WorkspaceId.From("retained-error"), Status = WorkspaceStatus.Error },
        new() { WorkspaceId = WorkspaceId.From("retained-recovery"), RecoveryCondition = WorkspaceRecoveryCondition.RequiresReconciliation },
        new() { WorkspaceId = WorkspaceId.From("retained-runtime-id"), RuntimeInstanceId = Guid.NewGuid() },
        new() { WorkspaceId = WorkspaceId.From("retained-runtime-name"), RuntimeName = "" },
        new() { WorkspaceId = WorkspaceId.From("retained-name"), Name = "" },
        new() { WorkspaceId = WorkspaceId.From("retained-start-time"), StartedAt = DateTimeOffset.UnixEpoch },
        new() { WorkspaceId = WorkspaceId.From("retained-stop-time"), StoppedAt = DateTimeOffset.UnixEpoch },
        new() { WorkspaceId = WorkspaceId.From("retained-error-message"), ErrorMessage = "retained failure" },
        new() { WorkspaceId = WorkspaceId.From("retained-agents"), ActiveAgents = ["assistant"] },
        new() { WorkspaceId = WorkspaceId.From("retained-tools"), ActiveTools = ["documents"] },
        new() { WorkspaceId = WorkspaceId.From("retained-plugins"), ActivePlugins = ["plugin"] },
        new() { WorkspaceId = WorkspaceId.From("retained-network"), NetworkId = NetworkId.From("retained-network-id") },
        new() { WorkspaceId = WorkspaceId.From("retained-container"), Containers = [new ContainerInfo { ContainerId = ContainerId.From("retained-container-id") }] },
        new() { WorkspaceId = WorkspaceId.From("retained-dapr"), DaprToolInstallations = [new DaprToolInstallation { Id = "retained-dapr/plugin", PluginName = "plugin" }] },
        new() { WorkspaceId = WorkspaceId.From("retained-mcp"), McpToolInstallations = [new McpToolInstallation { Id = "retained-mcp/plugin", PluginName = "plugin" }] }
    ];
}
