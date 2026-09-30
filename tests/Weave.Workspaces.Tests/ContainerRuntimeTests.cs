using Microsoft.Extensions.Logging.Abstractions;
using Weave.Shared.Ids;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Runtime;

namespace Weave.Workspaces.Tests;

public sealed class ContainerRuntimeTests
{
    private sealed class StubCommandRunner : ICommandRunner
    {
        public List<(string Command, IReadOnlyList<string> Arguments)> Invocations { get; } = [];
        public string NextOutput { get; set; } = string.Empty;
        public Queue<string> OutputQueue { get; } = new();
        public InvalidOperationException? ThrowOnNextCall { get; set; }
        public Func<IReadOnlyList<string>, Exception?>? FailureForArguments { get; set; }

        public Task<string> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            if (ThrowOnNextCall is { } ex)
            {
                ThrowOnNextCall = null;
                throw ex;
            }

            Invocations.Add((command, arguments));

            if (FailureForArguments?.Invoke(arguments) is { } failure)
                throw failure;

            var output = arguments.Count > 1 && arguments[0] == "network" && arguments[1] == "inspect"
                ? new string('d', 64) : OutputQueue.Count > 0 ? OutputQueue.Dequeue() : NextOutput;
            return Task.FromResult(output);
        }
    }

    private static ContainerRuntime CreateRuntime(StubCommandRunner stub) =>
        CreateContainerRuntime(stub, ContainerRuntimeOptions.PodmanEngine);

    private static ContainerRuntime CreateContainerRuntime(StubCommandRunner stub, string engine) =>
        new(
            stub,
            new ContainerRuntimeOptions { Engine = engine },
            NullLogger<ContainerRuntime>.Instance);

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
    public async Task StopContainerAsync_Podman_ForceRemovesMissingContainerSafely()
    {
        var stub = new StubCommandRunner();
        var runtime = CreateRuntime(stub);

        await runtime.StopContainerAsync(ContainerId.From("ctr-abc"), TestContext.Current.CancellationToken);

        stub.Invocations.Count.ShouldBe(1);
        stub.Invocations[0].Arguments.ShouldBe(["rm", "-f", "--ignore", "ctr-abc"]);
    }

    [Fact]
    public async Task StopContainerAsync_DockerMissingContainer_ConfirmsAbsence()
    {
        var stub = new StubCommandRunner
        {
            FailureForArguments = args => args.SequenceEqual(["rm", "-f", "ctr-abc"])
                ? new InvalidOperationException("No such container") : null
        };
        var runtime = CreateContainerRuntime(stub, ContainerRuntimeOptions.DockerEngine);

        await runtime.StopContainerAsync(ContainerId.From("ctr-abc"), TestContext.Current.CancellationToken);

        stub.Invocations.Count.ShouldBe(2);
        stub.Invocations[1].Arguments.ShouldBe(
            ["container", "ls", "--all", "--no-trunc", "--filter", "id=ctr-abc", "--format", "{{.ID}}"]);
    }

    [Fact]
    public async Task StopContainerAsync_DockerStillPresent_PropagatesRemovalFailure()
    {
        var stub = new StubCommandRunner
        {
            NextOutput = "ctr-abc\n",
            FailureForArguments = args => args.SequenceEqual(["rm", "-f", "ctr-abc"])
                ? new InvalidOperationException("remove failed") : null
        };
        var runtime = CreateContainerRuntime(stub, ContainerRuntimeOptions.DockerEngine);

        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            runtime.StopContainerAsync(ContainerId.From("ctr-abc"), TestContext.Current.CancellationToken));

        error.Message.ShouldBe("remove failed");
    }

    // --- CreateNetworkAsync ---

    [Fact]
    public async Task CreateNetworkAsync_BasicSpec_BuildsArgs()
    {
        var stub = new StubCommandRunner { NextOutput = "net-id-123\n" };
        var runtime = CreateRuntime(stub);

        var spec = new NetworkSpec { Name = "test-net" };

        var handle = await runtime.CreateNetworkAsync(spec, TestContext.Current.CancellationToken);

        stub.Invocations.Count.ShouldBe(2);
        var args = stub.Invocations[0].Arguments;
        args.ShouldContain("network");
        args.ShouldContain("create");
        args.ShouldContain("test-net");

        handle.Name.ShouldBe("test-net");
        handle.NetworkId.ShouldBe(NetworkId.From(new string('d', 64)));
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
        args.ShouldNotContain("--ignore");
        args.ShouldNotContain("-f");
        args.ShouldContain("net-123");
    }

    // --- TeardownAsync ---

    [Fact]
    public async Task TeardownAsync_RemovesContainersAndNetwork()
    {
        var stub = new StubCommandRunner();
        var runtime = CreateRuntime(stub);

        await runtime.TeardownAsync(WorkspaceId.From("ws1"), NetworkId.From("net-123"),
            [ContainerId.From("ctr-1"), ContainerId.From("ctr-2")], TestContext.Current.CancellationToken);

        stub.Invocations.Count.ShouldBe(3);

        // remove ctr-1
        stub.Invocations[0].Arguments.ShouldContain("rm");
        stub.Invocations[0].Arguments.ShouldContain("ctr-1");

        // remove ctr-2
        stub.Invocations[1].Arguments.ShouldContain("rm");
        stub.Invocations[1].Arguments.ShouldContain("ctr-2");

        // network rm
        stub.Invocations[2].Arguments.ShouldContain("network");
        stub.Invocations[2].Arguments.ShouldContain("rm");
        stub.Invocations[2].Arguments.ShouldContain("net-123");
    }

    [Fact]
    public async Task TeardownAsync_ContainerRemovalFails_AttemptsRemainingResourcesAndFails()
    {
        var stub = new StubCommandRunner
        {
            FailureForArguments = args => args.SequenceEqual(["rm", "-f", "--ignore", "ctr-1"])
                ? new InvalidOperationException("ctr-1 removal failed") : null
        };
        var runtime = CreateRuntime(stub);

        var error = await Should.ThrowAsync<AggregateException>(() => runtime.TeardownAsync(
            WorkspaceId.From("ws1"), NetworkId.From("net-123"),
            [ContainerId.From("ctr-1"), ContainerId.From("ctr-2")], TestContext.Current.CancellationToken));

        error.InnerExceptions.ShouldHaveSingleItem().Message.ShouldContain("ctr-1 removal failed");
        stub.Invocations.Count.ShouldBe(3);
        stub.Invocations[1].Arguments.ShouldContain("ctr-2");
        stub.Invocations[2].Arguments.ShouldContain("net-123");
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

        var env = await runtime.ProvisionAsync(WorkspaceId.From("test-ws"), manifest, TestContext.Current.CancellationToken);

        env.Containers.ShouldBeEmpty();
        stub.Invocations.Count.ShouldBe(2);
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

        await runtime.ProvisionAsync(WorkspaceId.From("my-ws"), manifest, TestContext.Current.CancellationToken);

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

    [Fact]
    public async Task ContainerRuntime_WithDockerEngine_UsesDockerCommand()
    {
        var stub = new StubCommandRunner { NextOutput = "abc123\n" };
        var runtime = CreateContainerRuntime(stub, ContainerRuntimeOptions.DockerEngine);

        await runtime.StartContainerAsync(
            new ContainerSpec { Name = "docker-ctr", Image = "alpine:latest" },
            TestContext.Current.CancellationToken);

        stub.Invocations[0].Command.ShouldBe("docker");
        runtime.RuntimeName.ShouldBe("docker");
    }

    [Fact]
    public async Task ContainerRuntime_WithDockerEngine_RemovesMissingNetworkSafely()
    {
        var stub = new StubCommandRunner();
        var runtime = CreateContainerRuntime(stub, ContainerRuntimeOptions.DockerEngine);

        await runtime.DeleteNetworkAsync(NetworkId.From("net-123"), TestContext.Current.CancellationToken);

        var args = stub.Invocations[0].Arguments;
        args.ShouldBe(["network", "rm", "-f", "net-123"]);
    }

    [Fact]
    public async Task DeleteNetworkAsync_WithPodman_DoesNotForceRemoveAttachedContainers()
    {
        var stub = new StubCommandRunner();
        var runtime = CreateRuntime(stub);

        await runtime.DeleteNetworkAsync(NetworkId.From("net-123"), TestContext.Current.CancellationToken);

        stub.Invocations[0].Arguments.ShouldBe(["network", "rm", "net-123"]);
    }

    [Fact]
    public void ContainerRuntime_WithUnsupportedEngine_Throws()
    {
        var stub = new StubCommandRunner();

        var ex = Should.Throw<InvalidOperationException>(() => CreateContainerRuntime(stub, "containerd"));

        ex.Message.ShouldContain("Unsupported container runtime");
    }
}
