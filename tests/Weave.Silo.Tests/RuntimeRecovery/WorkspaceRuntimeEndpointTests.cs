using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Weave.Management;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Runtime;

namespace Weave.Silo.Tests.RuntimeRecovery;

public sealed class WorkspaceRuntimeEndpointTests
{
    private static readonly string ContainerId = new('d', 64);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class RuntimeRunner : ICommandRunner
    {
        public bool Running { get; set; }
        public bool Missing { get; set; }
        public bool LoseStartResponse { get; set; }
        public int StartCount { get; private set; }
        public Func<bool>? AdmissionRecorded { get; set; }
        public Action? BeforeObservation { get; set; }
        public bool HoldStartResponse { get; set; }
        public TaskCompletionSource StartEntered { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? StartRelease { get; set; }

        public async Task<string> RunAsync(string command, IReadOnlyList<string> args, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var output = args[0] switch
            {
                "network" => "network-id\n",
                "run" => $"{ContainerId}\n",
                "container" => Observe(),
                "start" => Start(args),
                _ => throw new InvalidOperationException("Unexpected test CLI operation")
            };
            if (args[0] == "start" && HoldStartResponse)
            {
                Running = false;
                StartEntered.TrySetResult();
                if (StartRelease is null)
                    await Task.Delay(Timeout.Infinite, ct);
                else
                    await StartRelease.Task.WaitAsync(ct);
                Running = true;
            }
            return output;
        }

        private string Observe()
        {
            BeforeObservation?.Invoke();
            return Missing ? "" : $"{ContainerId}|{(Running ? "running" : "exited")}\n";
        }

        private string Start(IReadOnlyList<string> args)
        {
            args.ShouldBe(new[] { "start", ContainerId });
            AdmissionRecorded.ShouldNotBeNull().Invoke().ShouldBeTrue();
            StartCount++;
            Running = true;
            return LoseStartResponse ? throw new IOException("private process detail") : $"{ContainerId}\n";
        }
    }

    [Fact]
    public async Task RuntimeEndpoints_AuthorizedRecovery_RecordsBeforeStartAndRejectsDuplicate()
    {
        var runner = new RuntimeRunner();
        var clock = new FakeTimeProvider();
        await using var parent = new SiloFactory();
        await using var host = parent.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IWorkspaceRuntime>();
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IWorkspaceRuntime>(new ContainerRuntime(runner,
                new ContainerRuntimeOptions { Engine = "podman" }, NullLogger<ContainerRuntime>.Instance));
        }));
        using var client = host.CreateClient();
        var workspaceId = $"runtime-{Guid.NewGuid():N}";
        var workspace = host.Services.GetRequiredService<IVirtualActorProvider>()
            .GetActor<IWorkspaceActor>(VirtualActorId.From(workspaceId));
        await workspace.StartAsync(new WorkspaceManifest
        {
            Name = workspaceId,
            Version = "1.0",
            Tools = new() { ["peer"] = new ToolDefinition { Type = "mcp", Mcp = new McpConfig { Server = "test-image" } } }
        });
        var observationRoute = $"/api/workspaces/{workspaceId}/runtime";
        var recoveryRoute = $"/api/workspaces/{workspaceId}/containers/{ContainerId}/recover";

        using (var anonymous = await client.GetAsync(observationRoute, TestContext.Current.CancellationToken))
            anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        SiloFactory.Authorize(client, host.Services, "other-workspace", "workspace:runtime:read");
        using (var foreign = await client.GetAsync(observationRoute, TestContext.Current.CancellationToken))
            foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        SiloFactory.Authorize(client, host.Services, workspaceId, "workspace:runtime:read");
        using (var observed = await client.GetAsync(observationRoute, TestContext.Current.CancellationToken))
        {
            observed.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await observed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            body.RootElement.GetProperty("registeredStatus").GetString().ShouldBe("Running");
            body.RootElement.GetProperty("containers")[0].GetProperty("condition").GetString().ShouldBe("Stopped");
        }
        using (var denied = await client.PostAsync(recoveryRoute, null, TestContext.Current.CancellationToken))
            denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        runner.StartCount.ShouldBe(0);

        SiloFactory.Authorize(client, host.Services, workspaceId, "workspace:runtime:recover", "workspace:runtime:read");
        var journal = host.Services.GetRequiredService<IManagementOperationJournal>();
        var id = Guid.NewGuid().ToString("N");
        runner.AdmissionRecorded = () => journal.Find(id, CancellationToken.None)?.Target == $"{workspaceId}/{ContainerId}";
        client.DefaultRequestHeaders.Add("X-Weave-Management-Id", id);
        using (var recovered = await client.PostAsync(recoveryRoute, null, TestContext.Current.CancellationToken))
        {
            recovered.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await recovered.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            body.RootElement.GetProperty("outcome").GetString().ShouldBe("Started");
            body.RootElement.GetProperty("containerId").GetString().ShouldBe(ContainerId);
        }
        journal.Find(id, CancellationToken.None)?.Outcome.ToString().ShouldBe("Succeeded");
        using (var duplicate = await client.PostAsync(recoveryRoute, null, TestContext.Current.CancellationToken))
            duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        runner.StartCount.ShouldBe(1);
        using (var after = await client.GetAsync(observationRoute, TestContext.Current.CancellationToken))
        {
            using var body = JsonDocument.Parse(await after.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            body.RootElement.GetProperty("containers")[0].GetProperty("condition").GetString().ShouldBe("Running");
        }

        runner.Running = false;
        runner.LoseStartResponse = true;
        var unknownId = Guid.NewGuid().ToString("N");
        client.DefaultRequestHeaders.Remove("X-Weave-Management-Id");
        client.DefaultRequestHeaders.Add("X-Weave-Management-Id", unknownId);
        runner.AdmissionRecorded = () => journal.Find(unknownId, CancellationToken.None) is not null;
        using (var unknown = await client.PostAsync(recoveryRoute, null, TestContext.Current.CancellationToken))
        {
            unknown.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            var body = await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            body.ShouldContain("OutcomeUnknown");
            body.ShouldNotContain("private process detail");
        }
        journal.Find(unknownId, CancellationToken.None)?.Outcome.ToString().ShouldBe("OutcomeUnknown");
        using (var retry = await client.PostAsync(recoveryRoute, null, TestContext.Current.CancellationToken))
            retry.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        runner.StartCount.ShouldBe(2);
        var rpcToken = host.Services.GetRequiredService<ICapabilityTokenService>().Mint(new CapabilityTokenRequest
        {
            WorkspaceId = workspaceId,
            IssuedTo = "rpc-test-operator",
            Grants = ["workspace:runtime:recover"],
            Lifetime = TimeSpan.FromMinutes(5)
        });
        (await workspace.RecoverContainerAsync(Weave.Shared.Ids.ContainerId.From(ContainerId), rpcToken,
            unknownId.ToUpperInvariant(), TestContext.Current.CancellationToken)).Outcome.ShouldBe(ContainerRecoveryOutcome.AlreadyAdmitted);
        runner.StartCount.ShouldBe(2);

        runner.Missing = true;
        var missingId = Guid.NewGuid().ToString("N");
        client.DefaultRequestHeaders.Remove("X-Weave-Management-Id");
        client.DefaultRequestHeaders.Add("X-Weave-Management-Id", missingId);
        using (var missing = await client.PostAsync(recoveryRoute, null, TestContext.Current.CancellationToken))
        {
            missing.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            var body = await missing.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            body.ShouldContain("Missing");
            body.ShouldContain("Blocked");
        }
        journal.Find(missingId, CancellationToken.None)?.Outcome.ToString().ShouldBe("Failed");
        runner.StartCount.ShouldBe(2);

        runner.Missing = false;
        runner.Running = false;
        runner.LoseStartResponse = false;
        var revokedId = Guid.NewGuid().ToString("N");
        client.DefaultRequestHeaders.Remove("X-Weave-Management-Id");
        client.DefaultRequestHeaders.Add("X-Weave-Management-Id", revokedId);
        var token = JsonSerializer.Deserialize<CapabilityToken>(WebEncoders.Base64UrlDecode(
            client.DefaultRequestHeaders.GetValues("X-Weave-Capability").Single()), JsonOptions)!;
        runner.BeforeObservation = () => host.Services.GetRequiredService<ICapabilityTokenService>().Revoke(token.TokenId);
        using (var revoked = await client.PostAsync(recoveryRoute, null, TestContext.Current.CancellationToken))
            revoked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        journal.Find(revokedId, CancellationToken.None)?.Outcome.ToString().ShouldBe("Failed");
        runner.StartCount.ShouldBe(2);

        runner.BeforeObservation = null;
        SiloFactory.Authorize(client, host.Services, workspaceId, "workspace:runtime:recover");
        var timedOutId = Guid.NewGuid().ToString("N");
        client.DefaultRequestHeaders.Remove("X-Weave-Management-Id");
        client.DefaultRequestHeaders.Add("X-Weave-Management-Id", timedOutId);
        runner.AdmissionRecorded = () => journal.Find(timedOutId, CancellationToken.None) is not null;
        runner.HoldStartResponse = true;
        var pending = client.PostAsync(recoveryRoute, null, TestContext.Current.CancellationToken);
        await runner.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(10));
        using (var timedOut = await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))
        {
            timedOut.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
            (await timedOut.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
                .ShouldContain("container-recovery-outcome-unknown");
        }
        journal.Find(timedOutId, CancellationToken.None)?.Outcome.ToString().ShouldBe("OutcomeUnknown");
        using (var duplicateTimeout = await client.PostAsync(recoveryRoute, null, TestContext.Current.CancellationToken))
            duplicateTimeout.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        runner.StartCount.ShouldBe(3);

        runner.Running = false;
        runner.StartEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.StartRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstId = Guid.NewGuid().ToString("N");
        client.DefaultRequestHeaders.Remove("X-Weave-Management-Id");
        runner.AdmissionRecorded = () => journal.Find(firstId, CancellationToken.None) is not null;
        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, recoveryRoute);
        firstRequest.Headers.Add("X-Weave-Management-Id", firstId);
        var first = client.SendAsync(firstRequest, TestContext.Current.CancellationToken);
        await runner.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        using var secondRequest = new HttpRequestMessage(HttpMethod.Post, recoveryRoute);
        secondRequest.Headers.Add("X-Weave-Management-Id", Guid.NewGuid().ToString("N"));
        var second = client.SendAsync(secondRequest, TestContext.Current.CancellationToken);
        runner.StartRelease.TrySetResult();
        using var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        using var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        firstResult.StatusCode.ShouldBe(HttpStatusCode.OK);
        secondResult.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await firstResult.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("Started");
        (await secondResult.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("AlreadyRunning");
        runner.StartCount.ShouldBe(4);

        runner.Running = false;
        runner.HoldStartResponse = false;
        var rpcId = Guid.NewGuid().ToString("N");
        runner.AdmissionRecorded = () => journal.Find(rpcId, CancellationToken.None)?.Target == $"{workspaceId}/{ContainerId}";
        (await workspace.RecoverContainerAsync(Weave.Shared.Ids.ContainerId.From(ContainerId), rpcToken,
            rpcId, TestContext.Current.CancellationToken)).Outcome.ShouldBe(ContainerRecoveryOutcome.Started);
        journal.Find(rpcId, CancellationToken.None)?.Outcome.ToString().ShouldBe("Succeeded");
        (await workspace.RecoverContainerAsync(Weave.Shared.Ids.ContainerId.From(ContainerId), rpcToken,
            rpcId, TestContext.Current.CancellationToken)).Outcome.ShouldBe(ContainerRecoveryOutcome.AlreadyAdmitted);
        runner.StartCount.ShouldBe(5);
    }
}
