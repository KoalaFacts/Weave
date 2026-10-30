using Microsoft.Extensions.Logging.Abstractions;
using Weave.Shared.Ids;
using Weave.Workspaces.Runtime;

namespace Weave.Workspaces.Tests;

public sealed class ContainerRuntimeNetworkTests
{
    private static readonly NetworkId Network = NetworkId.From(new string('d', 64));
    private static readonly ContainerId Container = ContainerId.From(new string('a', 64));

    private static ContainerRuntime Runtime(ICommandRunner runner, string engine = "podman") => new(runner,
        new ContainerRuntimeOptions { Engine = engine }, NullLogger<ContainerRuntime>.Instance);

    [Theory]
    [InlineData("docker")]
    [InlineData("podman")]
    public async Task CreateNetworkAsync_IdentityUnconfirmed_FailsWithoutBinding(string engine)
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(engine, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns("unconfirmed-name\n");

        var error = await Should.ThrowAsync<InvalidOperationException>(() => Runtime(runner, engine).CreateNetworkAsync(
            new NetworkSpec { Name = "requested-network" }, TestContext.Current.CancellationToken));

        error.Message.ShouldBe("Created network identity could not be confirmed.");
    }

    [Fact]
    public async Task CreateNetworkAsync_DockerReturnsId_BindsWithoutNameLookup()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync("docker", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns($"{Network}\n");

        var handle = await Runtime(runner, "docker").CreateNetworkAsync(new NetworkSpec { Name = "requested-network" }, TestContext.Current.CancellationToken);

        handle.NetworkId.ShouldBe(Network);
        await runner.DidNotReceive().RunAsync("docker", Arg.Is<IReadOnlyList<string>>(args => args[1] == "inspect"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RuntimeNetworkOperations_InProcess_ObserveLocalMarkerAndBlockRecovery()
    {
        var runtime = new InProcessRuntime(NullLogger<InProcessRuntime>.Instance);

        (await runtime.ObserveNetworkAsync(NetworkId.From("local"), TestContext.Current.CancellationToken)).ShouldBe(NetworkRuntimeCondition.NotRequired);
        (await runtime.ObserveNetworkAsync(Network, TestContext.Current.CancellationToken)).ShouldBe(NetworkRuntimeCondition.InvalidIdentity);
        (await runtime.ObserveContainerNetworkAsync(Container, Network, TestContext.Current.CancellationToken)).ShouldBe(ContainerNetworkCondition.Unsupported);
        var result = await runtime.RecoverContainerAsync(Container, Network,
            () => throw new InvalidOperationException("must not dispatch"), TestContext.Current.CancellationToken);
        result.Network.Condition.ShouldBe(NetworkRuntimeCondition.Unsupported);
        result.Outcome.ShouldBe(ContainerRecoveryOutcome.Blocked);
        result.Dispatched.ShouldBeFalse();
    }

    [Theory]
    [InlineData("", NetworkRuntimeCondition.Missing)]
    [InlineData("short-id", NetworkRuntimeCondition.Unknown)]
    [InlineData("duplicate", NetworkRuntimeCondition.Unknown)]
    [InlineData("exact", NetworkRuntimeCondition.Present)]
    public async Task ObserveNetworkAsync_ProjectedOutput_RequiresExactSingleId(string output, NetworkRuntimeCondition expected)
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync("podman", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(output == "exact" ? $"{Network}\n" : output == "duplicate" ? $"{Network}\n{Network}\n" : output);

        (await Runtime(runner).ObserveNetworkAsync(Network, TestContext.Current.CancellationToken)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("network-name")]
    [InlineData("abc123")]
    [InlineData("--all")]
    public async Task ObserveNetworkAsync_NotImmutableId_RejectsWithoutCli(string id)
    {
        var runner = Substitute.For<ICommandRunner>();

        (await Runtime(runner).ObserveNetworkAsync(NetworkId.From(id), TestContext.Current.CancellationToken))
            .ShouldBe(NetworkRuntimeCondition.InvalidIdentity);
        runner.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("attached", ContainerNetworkCondition.Attached)]
    [InlineData("other", ContainerNetworkCondition.Detached)]
    [InlineData("", ContainerNetworkCondition.Detached)]
    [InlineData("network-name", ContainerNetworkCondition.Unknown)]
    [InlineData("bridge", ContainerNetworkCondition.Unknown)]
    [InlineData("duplicate", ContainerNetworkCondition.Unknown)]
    [InlineData("wrong-container", ContainerNetworkCondition.Unknown)]
    public async Task ObserveContainerNetworkAsync_ProjectedAttachment_DoesNotTrustNames(string value, ContainerNetworkCondition expected)
    {
        var output = value switch
        {
            "attached" => $"{Container}|{Network} ",
            "other" => $"{Container}|{new string('b', 64)} ",
            "duplicate" => $"{Container}|{Network} {Network}",
            "wrong-container" => $"{new string('b', 64)}|{Network}",
            _ => $"{Container}|{value}"
        };
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync("podman", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(output);

        (await Runtime(runner).ObserveContainerNetworkAsync(Container, Network, TestContext.Current.CancellationToken)).ShouldBe(expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverContainerAsync_NetworkMissingOrDetached_DoesNotStart(bool missing)
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync("podman", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var args = call.ArgAt<IReadOnlyList<string>>(1);
            return args[0] == "network" ? missing ? "" : $"{Network}\n"
                : args[1] == "inspect" ? $"{Container}|" : $"{Container}|exited\n";
        });

        var result = await Runtime(runner).RecoverContainerAsync(Container, Network, () => Task.CompletedTask, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ContainerRecoveryOutcome.Blocked);
        result.Dispatched.ShouldBeFalse();
        result.Network.Condition.ShouldBe(missing ? NetworkRuntimeCondition.Missing : NetworkRuntimeCondition.Present);
        result.NetworkAttachment.ShouldBe(missing ? ContainerNetworkCondition.NotChecked : ContainerNetworkCondition.Detached);
        await runner.DidNotReceive().RunAsync("podman", Arg.Is<IReadOnlyList<string>>(args => args[0] == "start"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverContainerAsync_NetworkDisappearsBeforeOrAfterDispatch_PreservesBoundary(bool afterDispatch)
    {
        var runner = Substitute.For<ICommandRunner>();
        var networkQueries = 0;
        runner.RunAsync("podman", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var args = call.ArgAt<IReadOnlyList<string>>(1);
            if (args[0] == "network")
                return ++networkQueries < (afterDispatch ? 3 : 2) ? $"{Network}\n" : "";
            return args[0] == "start" ? $"{Container}\n"
                : args[1] == "inspect" ? $"{Container}|{Network}\n"
                : $"{Container}|{(networkQueries == 0 ? "exited" : "running")}\n";
        });

        var result = await Runtime(runner).RecoverContainerAsync(Container, Network, () => Task.CompletedTask, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(afterDispatch ? ContainerRecoveryOutcome.OutcomeUnknown : ContainerRecoveryOutcome.Blocked);
        result.Dispatched.ShouldBe(afterDispatch);
        result.Network.Condition.ShouldBe(NetworkRuntimeCondition.Missing);
        await runner.Received(afterDispatch ? 1 : 0).RunAsync("podman", Arg.Is<IReadOnlyList<string>>(args => args[0] == "start"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteNetworkAsync_RemovalFails_OnlyConfirmedAbsenceIsSuccess()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync("podman", Arg.Is<IReadOnlyList<string>>(args => args[1] == "rm"), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new InvalidOperationException("remove failed"));
        runner.RunAsync("podman", Arg.Is<IReadOnlyList<string>>(args => args[1] == "ls"), Arg.Any<CancellationToken>())
            .Returns($"{Network}\n", "");

        await Should.ThrowAsync<InvalidOperationException>(() => Runtime(runner).DeleteNetworkAsync(Network, TestContext.Current.CancellationToken));
        await Runtime(runner).DeleteNetworkAsync(Network, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ObserveNetworkAsync_RuntimeUnavailableOrCancelled_PreservesTypedOutcome()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync("podman", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new IOException("private provider detail"));
        var runtime = Runtime(runner);
        (await runtime.ObserveNetworkAsync(Network, TestContext.Current.CancellationToken)).ShouldBe(NetworkRuntimeCondition.Unavailable);
        (await runtime.ObserveContainerNetworkAsync(Container, Network, TestContext.Current.CancellationToken)).ShouldBe(ContainerNetworkCondition.Unavailable);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => runtime.ObserveNetworkAsync(Network, cancelled.Token));
    }

    [Fact]
    public async Task CreateNetworkAsync_PodmanReturnsName_RetainsInspectedImmutableId()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync("podman", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns("weave-network\n", $"{Network}\n");
        var runtime = new ContainerRuntime(runner, new ContainerRuntimeOptions { Engine = "podman" },
            NullLogger<ContainerRuntime>.Instance);

        var handle = await runtime.CreateNetworkAsync(new NetworkSpec { Name = "weave-network" },
            TestContext.Current.CancellationToken);

        handle.NetworkId.ShouldBe(Network);
        handle.Name.ShouldBe("weave-network");
    }
}
