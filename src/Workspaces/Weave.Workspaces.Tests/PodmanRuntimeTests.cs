using Microsoft.Extensions.Logging.Abstractions;
using Weave.Shared.Ids;
using Weave.Workspaces.Models;
using Weave.Workspaces.Runtime;

namespace Weave.Workspaces.Tests;

public sealed class PodmanRuntimeTests
{
    private sealed class StubCommandRunner : ICommandRunner
    {
        public List<(string Command, IReadOnlyList<string> Arguments)> Invocations { get; } = [];
        public string NextOutput { get; set; } = string.Empty;
        public Queue<string> OutputQueue { get; } = new();
        public InvalidOperationException? ThrowOnNextCall { get; set; }

        public Task<string> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            if (ThrowOnNextCall is { } ex)
            {
                ThrowOnNextCall = null;
                throw ex;
            }

            Invocations.Add((command, arguments));

            var output = OutputQueue.Count > 0 ? OutputQueue.Dequeue() : NextOutput;
            return Task.FromResult(output);
        }
    }

    private static PodmanRuntime CreateRuntime(StubCommandRunner stub) =>
        new(stub, NullLogger<PodmanRuntime>.Instance);

    // --- StartContainerAsync ---

    [Fact]
    public async Task StartContainerAsync_BasicSpec_BuildsCorrectArgs()
    {
        var stub = new StubCommandRunner { NextOutput = "abc123\n" };
        var runtime = CreateRuntime(stub);

        var spec = new ContainerSpec { Name = "test-ctr", Image = "alpine:latest" };

        var handle = await runtime.StartContainerAsync(spec, TestContext.Current.CancellationToken);

        stub.Invocations.Count.ShouldBe(1);
        var (command, args) = stub.Invocations[0];
        command.ShouldBe("podman");
        args.ShouldContain("run");
        args.ShouldContain("-d");
        args.ShouldContain("--name");
        args.ShouldContain("test-ctr");
        args.ShouldContain("--cap-drop=ALL");
        args.ShouldContain("alpine:latest");
        handle.ContainerId.ShouldBe(ContainerId.From("abc123"));
    }

    [Fact]
    public async Task StartContainerAsync_WithNetwork_AddsNetworkFlag()
    {
        var stub = new StubCommandRunner { NextOutput = "ctr-1\n" };
        var runtime = CreateRuntime(stub);

        var spec = new ContainerSpec
        {
            Name = "net-ctr",
            Image = "alpine",
            NetworkId = NetworkId.From("my-net")
        };

        await runtime.StartContainerAsync(spec, TestContext.Current.CancellationToken);

        var args = stub.Invocations[0].Arguments;
        var networkIdx = args.ToList().IndexOf("--network");
        networkIdx.ShouldBeGreaterThan(-1);
        args[networkIdx + 1].ShouldBe("my-net");
    }

    [Fact]
    public async Task StartContainerAsync_ReadOnly_AddsFlag()
    {
        var stub = new StubCommandRunner { NextOutput = "ctr-2\n" };
        var runtime = CreateRuntime(stub);

        var spec = new ContainerSpec
        {
            Name = "ro-ctr",
            Image = "alpine",
            ReadOnly = true
        };

        await runtime.StartContainerAsync(spec, TestContext.Current.CancellationToken);

        stub.Invocations[0].Arguments.ShouldContain("--read-only");
    }

    [Fact]
    public async Task StartContainerAsync_NoNetwork_AddsFlag()
    {
        var stub = new StubCommandRunner { NextOutput = "ctr-3\n" };
        var runtime = CreateRuntime(stub);

        var spec = new ContainerSpec
        {
            Name = "nonet-ctr",
            Image = "alpine",
            NoNetwork = true
        };

        await runtime.StartContainerAsync(spec, TestContext.Current.CancellationToken);

        stub.Invocations[0].Arguments.ShouldContain("--network=none");
    }

    [Fact]
    public async Task StartContainerAsync_WithEnvVars_AddsEachPair()
    {
        var stub = new StubCommandRunner { NextOutput = "ctr-4\n" };
        var runtime = CreateRuntime(stub);

        var spec = new ContainerSpec
        {
            Name = "env-ctr",
            Image = "alpine",
            Environment = new Dictionary<string, string>
            {
                ["DB_HOST"] = "localhost",
                ["DB_PORT"] = "5432"
            }
        };

        await runtime.StartContainerAsync(spec, TestContext.Current.CancellationToken);

        var args = stub.Invocations[0].Arguments;
        args.ShouldContain("-e");
        args.ShouldContain("DB_HOST=localhost");
        args.ShouldContain("DB_PORT=5432");
    }

    [Fact]
    public async Task StartContainerAsync_WithPortMappings_AddsPFlag()
    {
        var stub = new StubCommandRunner { NextOutput = "ctr-5\n" };
        var runtime = CreateRuntime(stub);

        var spec = new ContainerSpec
        {
            Name = "port-ctr",
            Image = "alpine",
            PortMappings = new Dictionary<int, int> { [8080] = 80 }
        };

        await runtime.StartContainerAsync(spec, TestContext.Current.CancellationToken);

        var args = stub.Invocations[0].Arguments;
        args.ShouldContain("-p");
        args.ShouldContain("8080:80");
    }

    [Fact]
    public async Task StartContainerAsync_WithCommand_AppendsAfterImage()
    {
        var stub = new StubCommandRunner { NextOutput = "ctr-6\n" };
        var runtime = CreateRuntime(stub);

        var spec = new ContainerSpec
        {
            Name = "cmd-ctr",
            Image = "alpine",
            Command = ["echo", "hello"]
        };

        await runtime.StartContainerAsync(spec, TestContext.Current.CancellationToken);

        var args = stub.Invocations[0].Arguments.ToList();
        var imageIdx = args.IndexOf("alpine");
        var echoIdx = args.IndexOf("echo");
        var helloIdx = args.IndexOf("hello");

        imageIdx.ShouldBeGreaterThan(-1);
        echoIdx.ShouldBeGreaterThan(imageIdx);
        helloIdx.ShouldBeGreaterThan(imageIdx);
    }

    // --- StopContainerAsync ---

    [Fact]
    public async Task StopContainerAsync_CallsStopThenRm()
    {
        var stub = new StubCommandRunner();
        var runtime = CreateRuntime(stub);

        await runtime.StopContainerAsync(ContainerId.From("ctr-abc"), TestContext.Current.CancellationToken);

        stub.Invocations.Count.ShouldBe(2);

        var (_, stopArgs) = stub.Invocations[0];
        stopArgs.ShouldContain("stop");
        stopArgs.ShouldContain("ctr-abc");

        var (_, rmArgs) = stub.Invocations[1];
        rmArgs.ShouldContain("rm");
        rmArgs.ShouldContain("-f");
        rmArgs.ShouldContain("ctr-abc");
    }

    // --- CreateNetworkAsync ---

    [Fact]
    public async Task CreateNetworkAsync_BasicSpec_BuildsArgs()
    {
        var stub = new StubCommandRunner { NextOutput = "net-id-123\n" };
        var runtime = CreateRuntime(stub);

        var spec = new NetworkSpec { Name = "test-net" };

        var handle = await runtime.CreateNetworkAsync(spec, TestContext.Current.CancellationToken);

        stub.Invocations.Count.ShouldBe(1);
        var args = stub.Invocations[0].Arguments;
        args.ShouldContain("network");
        args.ShouldContain("create");
        args.ShouldContain("test-net");

        handle.Name.ShouldBe("test-net");
        handle.NetworkId.ShouldBe(NetworkId.From("net-id-123"));
    }

    [Fact]
    public async Task CreateNetworkAsync_WithSubnet_AddsFlag()
    {
        var stub = new StubCommandRunner { NextOutput = "net-456\n" };
        var runtime = CreateRuntime(stub);

        var spec = new NetworkSpec { Name = "sub-net", Subnet = "10.0.0.0/24" };

        await runtime.CreateNetworkAsync(spec, TestContext.Current.CancellationToken);

        var args = stub.Invocations[0].Arguments;
        args.ShouldContain("--subnet");
        args.ShouldContain("10.0.0.0/24");
    }

    // --- DeleteNetworkAsync ---

    [Fact]
    public async Task DeleteNetworkAsync_CallsNetworkRm()
    {
        var stub = new StubCommandRunner();
        var runtime = CreateRuntime(stub);

        await runtime.DeleteNetworkAsync(NetworkId.From("net-123"), TestContext.Current.CancellationToken);

        stub.Invocations.Count.ShouldBe(1);
        var args = stub.Invocations[0].Arguments;
        args.ShouldContain("network");
        args.ShouldContain("rm");
        args.ShouldContain("-f");
        args.ShouldContain("net-123");
    }

    // --- TeardownAsync ---

    [Fact]
    public async Task TeardownAsync_StopsContainersAndRemovesNetwork()
    {
        var stub = new StubCommandRunner();
        // First call (ps) returns two container IDs
        stub.OutputQueue.Enqueue("ctr-1\nctr-2\n");
        var runtime = CreateRuntime(stub);

        await runtime.TeardownAsync(WorkspaceId.From("ws1"), TestContext.Current.CancellationToken);

        // ps, stop ctr-1, rm ctr-1, stop ctr-2, rm ctr-2, network rm
        stub.Invocations.Count.ShouldBe(6);

        // ps invocation
        stub.Invocations[0].Arguments.ShouldContain("ps");

        // stop ctr-1
        stub.Invocations[1].Arguments.ShouldContain("stop");
        stub.Invocations[1].Arguments.ShouldContain("ctr-1");

        // rm ctr-1
        stub.Invocations[2].Arguments.ShouldContain("rm");
        stub.Invocations[2].Arguments.ShouldContain("ctr-1");

        // stop ctr-2
        stub.Invocations[3].Arguments.ShouldContain("stop");
        stub.Invocations[3].Arguments.ShouldContain("ctr-2");

        // rm ctr-2
        stub.Invocations[4].Arguments.ShouldContain("rm");
        stub.Invocations[4].Arguments.ShouldContain("ctr-2");

        // network rm
        stub.Invocations[5].Arguments.ShouldContain("network");
        stub.Invocations[5].Arguments.ShouldContain("rm");
    }

    // --- ProvisionAsync ---

    [Fact]
    public async Task ProvisionAsync_SkipsNonMcpTools()
    {
        var stub = new StubCommandRunner { NextOutput = "net-123\n" };
        var runtime = CreateRuntime(stub);

        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = "test-ws",
            Tools = new Dictionary<string, ToolDefinition>
            {
                ["cli-tool"] = new() { Type = "cli" },
                ["openapi-tool"] = new() { Type = "openapi" }
            }
        };

        var env = await runtime.ProvisionAsync(manifest, TestContext.Current.CancellationToken);

        env.Containers.ShouldBeEmpty();
        // Only 1 invocation: network create
        stub.Invocations.Count.ShouldBe(1);
        stub.Invocations[0].Arguments.ShouldContain("network");
        stub.Invocations[0].Arguments.ShouldContain("create");
    }

    [Fact]
    public async Task ProvisionAsync_CreatesNetworkFromManifest()
    {
        var stub = new StubCommandRunner { NextOutput = "net-123\n" };
        var runtime = CreateRuntime(stub);

        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = "my-ws",
            Workspace = new WorkspaceConfig
            {
                Network = new NetworkConfig { Name = "custom-{workspace}-net" }
            }
        };

        await runtime.ProvisionAsync(manifest, TestContext.Current.CancellationToken);

        var args = stub.Invocations[0].Arguments;
        args.ShouldContain("custom-my-ws-net");
    }

    // --- Error handling ---

    [Fact]
    public async Task RunAsync_NonZeroExitCode_PropagatesException()
    {
        var stub = new StubCommandRunner
        {
            ThrowOnNextCall = new InvalidOperationException("exit code 1")
        };
        var runtime = CreateRuntime(stub);

        var spec = new ContainerSpec { Name = "fail-ctr", Image = "alpine" };

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => runtime.StartContainerAsync(spec, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("exit code 1");
    }
}
