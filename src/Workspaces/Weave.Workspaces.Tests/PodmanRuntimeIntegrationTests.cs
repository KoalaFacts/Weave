using Microsoft.Extensions.Logging.Abstractions;
using Weave.Workspaces.Models;
using Weave.Workspaces.Runtime;

namespace Weave.Workspaces.Tests;

[Trait("Category", "Integration")]
public sealed class PodmanRuntimeIntegrationTests
{
    private static readonly ProcessCommandRunner Runner = new();

    private static PodmanRuntime CreateRuntime() =>
        new(Runner, NullLogger<PodmanRuntime>.Instance);

    private static string UniqueResourceName(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}"[..30];

    [Fact]
    public async Task CreateNetwork_ThenDelete_Succeeds()
    {
        var runtime = CreateRuntime();
        var networkName = UniqueResourceName("intnet");
        NetworkHandle? handle = null;

        try
        {
            handle = await runtime.CreateNetworkAsync(
                new NetworkSpec { Name = networkName },
                TestContext.Current.CancellationToken);

            handle.ShouldNotBeNull();
            handle.Name.ShouldBe(networkName);

            // Verify the network exists via podman network ls
            var output = await Runner.RunAsync(
                "podman",
                ["network", "ls", "--format", "{{.Name}}"],
                TestContext.Current.CancellationToken);

            output.ShouldContain(networkName);
        }
        finally
        {
            if (handle is not null)
            {
                await runtime.DeleteNetworkAsync(handle.NetworkId, CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task StartContainer_ThenStop_Succeeds()
    {
        var runtime = CreateRuntime();
        var containerName = UniqueResourceName("intctr");
        ContainerHandle? handle = null;

        try
        {
            handle = await runtime.StartContainerAsync(new ContainerSpec
            {
                Name = containerName,
                Image = "alpine:latest",
                Command = ["sleep", "30"],
                DropAllCapabilities = false
            }, TestContext.Current.CancellationToken);

            handle.ShouldNotBeNull();
            handle.Name.ShouldBe(containerName);

            // Verify the container is running
            var output = await Runner.RunAsync(
                "podman",
                ["ps", "--filter", $"name={containerName}", "--format", "{{.Names}}"],
                TestContext.Current.CancellationToken);

            output.Trim().ShouldContain(containerName);
        }
        finally
        {
            if (handle is not null)
            {
                await runtime.StopContainerAsync(handle.ContainerId, CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task StartContainer_WithEnvVars_PassedToContainer()
    {
        var runtime = CreateRuntime();
        var containerName = UniqueResourceName("intenv");
        ContainerHandle? handle = null;

        try
        {
            handle = await runtime.StartContainerAsync(new ContainerSpec
            {
                Name = containerName,
                Image = "alpine:latest",
                Command = ["sleep", "30"],
                DropAllCapabilities = false,
                Environment = new Dictionary<string, string>
                {
                    ["WEAVE_TEST_VAR"] = "hello123"
                }
            }, TestContext.Current.CancellationToken);

            handle.ShouldNotBeNull();

            // Verify the environment variable is set inside the container
            var output = await Runner.RunAsync(
                "podman",
                ["exec", containerName, "env"],
                TestContext.Current.CancellationToken);

            output.ShouldContain("WEAVE_TEST_VAR=hello123");
        }
        finally
        {
            if (handle is not null)
            {
                await runtime.StopContainerAsync(handle.ContainerId, CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task ProvisionAsync_FullLifecycle_CreatesAndTearsDown()
    {
        var runtime = CreateRuntime();
        var workspaceName = UniqueResourceName("intws");
        WorkspaceEnvironment? env = null;

        try
        {
            var manifest = new WorkspaceManifest
            {
                Version = "1.0",
                Name = workspaceName,
                Tools = new Dictionary<string, ToolDefinition>
                {
                    ["test-tool"] = new()
                    {
                        Type = "mcp",
                        Mcp = new McpConfig
                        {
                            Server = "alpine:latest",
                            Args = ["sleep", "30"]
                        }
                    }
                }
            };

            env = await runtime.ProvisionAsync(manifest, TestContext.Current.CancellationToken);

            env.ShouldNotBeNull();
            env.Containers.Count.ShouldBe(1);

            // Verify the container is running via podman ps
            var containerName = $"weave-{workspaceName}-test-tool";
            var output = await Runner.RunAsync(
                "podman",
                ["ps", "--filter", $"name={containerName}", "--format", "{{.Names}}"],
                TestContext.Current.CancellationToken);

            output.Trim().ShouldContain(containerName);
        }
        finally
        {
            if (env is not null)
            {
                await runtime.TeardownAsync(env.WorkspaceId, CancellationToken.None);
            }
        }
    }
}
