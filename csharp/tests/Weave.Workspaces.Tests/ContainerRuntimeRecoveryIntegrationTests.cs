using Microsoft.Extensions.Logging.Abstractions;
using Weave.Invocations.Processes;
using Weave.Workspaces.Runtime;

namespace Weave.Workspaces.Tests;

[Trait("Category", "Integration")]
public sealed class ContainerRuntimeRecoveryIntegrationTests
{
    [Fact]
    public async Task RecoverContainerAsync_RealStoppedContainer_StartsSameIdAndBlocksAfterRemoval()
    {
        var runner = new ProcessCommandRunner(new ProcessRunner(TimeProvider.System, NullLogger<ProcessRunner>.Instance));
        var runtime = new ContainerRuntime(runner, new ContainerRuntimeOptions { Engine = "podman" },
            NullLogger<ContainerRuntime>.Instance);
        ContainerHandle? handle = null;
        NetworkHandle? network = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            network = await runtime.CreateNetworkAsync(new NetworkSpec { Name = $"weave-network-{Guid.NewGuid():N}"[..30] }, timeout.Token);
            handle = await runtime.StartContainerAsync(new ContainerSpec
            {
                Name = $"weave-observe-{Guid.NewGuid():N}"[..30],
                Image = "docker.io/library/alpine:latest",
                Command = ["sleep", "120"],
                NetworkId = network.NetworkId
            }, timeout.Token);
            (await runtime.ObserveContainerAsync(handle.ContainerId, timeout.Token))
                .ShouldBe(ContainerRuntimeCondition.Running);
            await runner.RunAsync("podman", ["stop", "--time", "1", handle.ContainerId.ToString()], timeout.Token);
            (await runtime.ObserveContainerAsync(handle.ContainerId, timeout.Token))
                .ShouldBe(ContainerRuntimeCondition.Stopped);

            var recovered = await runtime.RecoverContainerAsync(handle.ContainerId, network!.NetworkId, () => Task.CompletedTask, timeout.Token);

            recovered.ContainerId.ShouldBe(handle.ContainerId.ToString());
            recovered.Outcome.ShouldBe(ContainerRecoveryOutcome.Started);
            recovered.Condition.ShouldBe(ContainerRuntimeCondition.Running);
            recovered.Network.Condition.ShouldBe(NetworkRuntimeCondition.Present);
            recovered.NetworkAttachment.ShouldBe(ContainerNetworkCondition.Attached);
            (await runtime.RecoverContainerAsync(handle.ContainerId, network!.NetworkId, () => Task.CompletedTask, timeout.Token))
                .Outcome.ShouldBe(ContainerRecoveryOutcome.AlreadyRunning);
            await runner.RunAsync("podman", ["network", "disconnect", network!.NetworkId.ToString(), handle.ContainerId.ToString()], timeout.Token);
            var disconnected = await runtime.RecoverContainerAsync(handle.ContainerId, network.NetworkId, () => Task.CompletedTask, timeout.Token);
            disconnected.Outcome.ShouldBe(ContainerRecoveryOutcome.Blocked);
            disconnected.NetworkAttachment.ShouldBe(ContainerNetworkCondition.Detached);
            disconnected.Dispatched.ShouldBeFalse();
            await runner.RunAsync("podman", ["stop", "--time", "1", handle.ContainerId.ToString()], timeout.Token);
            var detached = await runtime.RecoverContainerAsync(handle.ContainerId, network.NetworkId, () => Task.CompletedTask, timeout.Token);
            detached.Outcome.ShouldBe(ContainerRecoveryOutcome.Blocked);
            detached.NetworkAttachment.ShouldBeOneOf(ContainerNetworkCondition.Detached, ContainerNetworkCondition.Unknown);
            detached.Dispatched.ShouldBeFalse();
            (await runtime.ObserveContainerAsync(handle.ContainerId, timeout.Token)).ShouldBe(ContainerRuntimeCondition.Stopped);
            await runtime.DeleteNetworkAsync(network.NetworkId, timeout.Token);
            var networkMissing = await runtime.RecoverContainerAsync(handle.ContainerId, network.NetworkId, () => Task.CompletedTask, timeout.Token);
            networkMissing.Outcome.ShouldBe(ContainerRecoveryOutcome.Blocked);
            networkMissing.Network.Condition.ShouldBe(NetworkRuntimeCondition.Missing);
            networkMissing.Dispatched.ShouldBeFalse();
            await runtime.StopContainerAsync(handle.ContainerId, timeout.Token);
            var missing = await runtime.RecoverContainerAsync(handle.ContainerId, network!.NetworkId, () => Task.CompletedTask, timeout.Token);
            missing.Outcome.ShouldBe(ContainerRecoveryOutcome.Blocked);
            missing.Condition.ShouldBe(ContainerRuntimeCondition.Missing);
            missing.Dispatched.ShouldBeFalse();
        }
        finally
        {
            if (handle is not null)
                await runtime.StopContainerAsync(handle.ContainerId, CancellationToken.None);
            if (network is not null)
                await runtime.DeleteNetworkAsync(network.NetworkId, CancellationToken.None);
        }
    }
}
