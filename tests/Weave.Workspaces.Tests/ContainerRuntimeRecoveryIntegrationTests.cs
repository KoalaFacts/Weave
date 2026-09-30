using Microsoft.Extensions.Logging.Abstractions;
using Weave.Workspaces.Runtime;

namespace Weave.Workspaces.Tests;

[Trait("Category", "Integration")]
public sealed class ContainerRuntimeRecoveryIntegrationTests
{
    [Fact]
    public async Task RecoverContainerAsync_RealStoppedContainer_StartsSameIdAndBlocksAfterRemoval()
    {
        var runner = new ProcessCommandRunner();
        var runtime = new ContainerRuntime(runner, new ContainerRuntimeOptions { Engine = "podman" },
            NullLogger<ContainerRuntime>.Instance);
        ContainerHandle? handle = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            handle = await runtime.StartContainerAsync(new ContainerSpec
            {
                Name = $"weave-observe-{Guid.NewGuid():N}"[..30],
                Image = "docker.io/library/alpine:latest",
                Command = ["sleep", "120"],
                NoNetwork = true
            }, timeout.Token);
            (await runtime.ObserveContainerAsync(handle.ContainerId, timeout.Token))
                .ShouldBe(ContainerRuntimeCondition.Running);
            await runner.RunAsync("podman", ["stop", "--time", "1", handle.ContainerId.ToString()], timeout.Token);
            (await runtime.ObserveContainerAsync(handle.ContainerId, timeout.Token))
                .ShouldBe(ContainerRuntimeCondition.Stopped);

            var recovered = await runtime.RecoverContainerAsync(handle.ContainerId, () => Task.CompletedTask, timeout.Token);

            recovered.ContainerId.ShouldBe(handle.ContainerId.ToString());
            recovered.Outcome.ShouldBe(ContainerRecoveryOutcome.Started);
            recovered.Condition.ShouldBe(ContainerRuntimeCondition.Running);
            (await runtime.RecoverContainerAsync(handle.ContainerId, () => Task.CompletedTask, timeout.Token))
                .Outcome.ShouldBe(ContainerRecoveryOutcome.AlreadyRunning);
            await runtime.StopContainerAsync(handle.ContainerId, timeout.Token);
            var missing = await runtime.RecoverContainerAsync(handle.ContainerId, () => Task.CompletedTask, timeout.Token);
            missing.Outcome.ShouldBe(ContainerRecoveryOutcome.Blocked);
            missing.Condition.ShouldBe(ContainerRuntimeCondition.Missing);
            missing.Dispatched.ShouldBeFalse();
        }
        finally
        {
            if (handle is not null)
                await runtime.StopContainerAsync(handle.ContainerId, CancellationToken.None);
        }
    }
}
