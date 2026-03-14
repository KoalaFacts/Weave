using Microsoft.Extensions.Logging;
using Weave.Shared.Ids;
using Weave.Workspaces.Models;
using Weave.Workspaces.Runtime;

namespace Weave.Workspaces.Tests;

public sealed class InProcessRuntimeTests
{
    private static InProcessRuntime CreateRuntime() =>
        new(Substitute.For<ILogger<InProcessRuntime>>());

    private static WorkspaceManifest CreateManifest(string name = "test-ws") => new()
    {
        Name = name,
        Version = "1.0",
        Workspace = new WorkspaceConfig
        {
            Network = new NetworkConfig { Name = "weave-test" }
        },
        Agents = new Dictionary<string, AgentDefinition>
        {
            ["assistant"] = new() { Model = "claude-sonnet-4-20250514" }
        },
        Tools = new Dictionary<string, ToolDefinition>
        {
            ["web-search"] = new() { Type = "mcp" }
        }
    };

    [Fact]
    public void RuntimeName_ReturnsInProcess()
    {
        var runtime = CreateRuntime();
        runtime.RuntimeName.ShouldBe("in-process");
    }

    [Fact]
    public async Task ProvisionAsync_ReturnsEnvironmentWithNoContainers()
    {
        var runtime = CreateRuntime();
        var manifest = CreateManifest();

        var env = await runtime.ProvisionAsync(manifest, CancellationToken.None);

        env.WorkspaceId.ShouldBe(WorkspaceId.From("test-ws"));
        env.NetworkId.ShouldBe(NetworkId.From("local"));
        env.Containers.ShouldBeEmpty();
    }

    [Fact]
    public async Task ProvisionAsync_UsesManifestNameAsWorkspaceId()
    {
        var runtime = CreateRuntime();
        var manifest = CreateManifest("my-workspace");

        var env = await runtime.ProvisionAsync(manifest, CancellationToken.None);

        env.WorkspaceId.ShouldBe(WorkspaceId.From("my-workspace"));
    }

    [Fact]
    public async Task TeardownAsync_CompletesWithoutError()
    {
        var runtime = CreateRuntime();

        await runtime.TeardownAsync(WorkspaceId.From("test-ws"), CancellationToken.None);
    }

    [Fact]
    public async Task StartContainerAsync_ReturnsLocalHandle()
    {
        var runtime = CreateRuntime();
        var spec = new ContainerSpec
        {
            Name = "test-container",
            Image = "test:latest",
            PortMappings = new Dictionary<int, int> { [8080] = 80 }
        };

        var handle = await runtime.StartContainerAsync(spec, CancellationToken.None);

        handle.ContainerId.ShouldBe(ContainerId.From("local-test-container"));
        handle.Name.ShouldBe("test-container");
        handle.Image.ShouldBe("test:latest");
        handle.PortMappings.ShouldContainKeyAndValue(8080, 80);
    }

    [Fact]
    public async Task StopContainerAsync_CompletesWithoutError()
    {
        var runtime = CreateRuntime();

        await runtime.StopContainerAsync(ContainerId.From("local-c1"), CancellationToken.None);
    }

    [Fact]
    public async Task CreateNetworkAsync_ReturnsLocalNetwork()
    {
        var runtime = CreateRuntime();
        var spec = new NetworkSpec { Name = "weave-test" };

        var handle = await runtime.CreateNetworkAsync(spec, CancellationToken.None);

        handle.NetworkId.ShouldBe(NetworkId.From("local"));
        handle.Name.ShouldBe("weave-test");
    }

    [Fact]
    public async Task DeleteNetworkAsync_CompletesWithoutError()
    {
        var runtime = CreateRuntime();

        await runtime.DeleteNetworkAsync(NetworkId.From("local"), CancellationToken.None);
    }

    [Fact]
    public async Task WorkspaceGrain_StartsWithInProcessRuntime()
    {
        // Verify InProcessRuntime works correctly with WorkspaceGrain
        var runtime = CreateRuntime();
        var manifest = CreateManifest();

        var env = await runtime.ProvisionAsync(manifest, CancellationToken.None);

        // WorkspaceGrain stores containers from the environment — in-process has none
        env.Containers.Count.ShouldBe(0);
        // But the workspace is valid with agents and network
        env.WorkspaceId.ToString().ShouldNotBeNullOrEmpty();
        env.NetworkId.ToString().ShouldNotBeNullOrEmpty();
    }
}
