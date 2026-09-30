using Microsoft.Extensions.Logging.Abstractions;
using Weave.Shared.Ids;
using Weave.Workspaces.Runtime;

namespace Weave.Workspaces.Tests;

public sealed class ContainerRuntimeRecoveryTests
{
    private static readonly ContainerId Id = ContainerId.From(new string('a', 64));

    private static ContainerRuntime Runtime(ICommandRunner runner) => new(runner,
        new ContainerRuntimeOptions { Engine = "podman" }, NullLogger<ContainerRuntime>.Instance);

    [Fact]
    public async Task RecoverContainerAsync_AuthorityRevokedAfterObservation_DoesNotDispatch()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync("podman", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns($"{Id}|exited\n");

        await Should.ThrowAsync<UnauthorizedAccessException>(() => Runtime(runner).RecoverContainerAsync(Id,
            () => throw new UnauthorizedAccessException("revoked"), TestContext.Current.CancellationToken));

        await runner.DidNotReceive().RunAsync("podman",
            Arg.Is<IReadOnlyList<string>>(args => args[0] == "start"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("", ContainerRuntimeCondition.Missing)]
    [InlineData("paused", ContainerRuntimeCondition.Transitioning)]
    [InlineData("dead", ContainerRuntimeCondition.Unknown)]
    [InlineData("running", ContainerRuntimeCondition.Running)]
    public async Task RecoverContainerAsync_NotStopped_DoesNotStart(string state, ContainerRuntimeCondition expected)
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync("podman", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(state.Length == 0 ? "" : $"{Id}|{state}\n");

        var result = await Runtime(runner).RecoverContainerAsync(Id, () => Task.CompletedTask, TestContext.Current.CancellationToken);

        result.Condition.ShouldBe(expected);
        result.Dispatched.ShouldBeFalse();
        result.Outcome.ShouldBe(expected is ContainerRuntimeCondition.Running
            ? ContainerRecoveryOutcome.AlreadyRunning : ContainerRecoveryOutcome.Blocked);
        await runner.DidNotReceive().RunAsync("podman",
            Arg.Is<IReadOnlyList<string>>(args => args[0] == "start"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("container-name")]
    [InlineData("abc123")]
    [InlineData("--all")]
    public async Task ObserveContainerAsync_NotFullId_BlocksWithoutCli(string id)
    {
        var runner = Substitute.For<ICommandRunner>();

        (await Runtime(runner).ObserveContainerAsync(ContainerId.From(id), TestContext.Current.CancellationToken))
            .ShouldBe(ContainerRuntimeCondition.InvalidIdentity);
        runner.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("another-id|running")]
    [InlineData("running")]
    [InlineData("duplicate")]
    public async Task ObserveContainerAsync_AmbiguousOutput_IsUnknown(string output)
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync("podman", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(output == "duplicate" ? $"{Id}|running\n{Id}|running\n" : output);

        (await Runtime(runner).ObserveContainerAsync(Id, TestContext.Current.CancellationToken))
            .ShouldBe(ContainerRuntimeCondition.Unknown);
    }

    [Fact]
    public async Task RecoverContainerAsync_StartResponseLost_RetainsUnknownAndDoesNotRetry()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync("podman", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyList<string>>(1)[0] == "start"
                ? throw new IOException("private runtime detail") : $"{Id}|exited\n");

        var result = await Runtime(runner).RecoverContainerAsync(Id, () => Task.CompletedTask, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ContainerRecoveryOutcome.OutcomeUnknown);
        result.Dispatched.ShouldBeTrue();
        await runner.Received(1).RunAsync("podman",
            Arg.Is<IReadOnlyList<string>>(args => args[0] == "start"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecoverContainerAsync_CancelledBeforeDispatch_PropagatesWithoutCli()
    {
        var runner = Substitute.For<ICommandRunner>();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Runtime(runner).RecoverContainerAsync(Id, () => Task.CompletedTask, cancelled.Token));
        runner.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task RecoverContainerAsync_RunningNotConfirmed_IsUnknown()
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync("podman", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns($"{Id}|exited\n", $"{Id}\n", $"{Id}|exited\n");

        var result = await Runtime(runner).RecoverContainerAsync(Id, () => Task.CompletedTask, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ContainerRecoveryOutcome.OutcomeUnknown);
        result.Condition.ShouldBe(ContainerRuntimeCondition.Stopped);
        result.Dispatched.ShouldBeTrue();
    }

    [Theory]
    [InlineData("podman")]
    [InlineData("docker")]
    public async Task RecoverContainerAsync_StoppedContainer_StartsSameIdAndVerifiesRunning(string engine)
    {
        var runner = Substitute.For<ICommandRunner>();
        runner.RunAsync(engine, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns($"{Id}|exited\n", $"{Id}\n", $"{Id}|running\n");
        var runtime = new ContainerRuntime(runner, new ContainerRuntimeOptions { Engine = engine },
            NullLogger<ContainerRuntime>.Instance);

        var result = await runtime.RecoverContainerAsync(Id, () => Task.CompletedTask, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ContainerRecoveryOutcome.Started);
        result.Condition.ShouldBe(ContainerRuntimeCondition.Running);
        result.ContainerId.ShouldBe(Id.ToString());
        result.Dispatched.ShouldBeTrue();
        await runner.Received().RunAsync(engine,
            Arg.Is<IReadOnlyList<string>>(args => args.SequenceEqual(new[] { "start", Id.ToString() })),
            Arg.Any<CancellationToken>());
    }
}
