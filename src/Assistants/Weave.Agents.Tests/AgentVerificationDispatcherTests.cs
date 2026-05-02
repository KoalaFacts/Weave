using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Agents.Verification;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

public sealed class AgentVerificationDispatcherTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");

    private static AgentVerificationRequest CreateRequest() =>
        new(
            TestWorkspaceId,
            AgentName: "researcher",
            TaskId: AgentTaskId.From("task-1"),
            Proof: new ProofOfWork
            {
                Items = [new ProofItem { Type = ProofType.TestResults, Label = "Tests", Value = "ok" }]
            });

    [Fact]
    public async Task ProcessAsync_EnqueuedRequest_CallsVerifier()
    {
        var verifier = Substitute.For<IProofVerifierActor>();
        var actors = Substitute.For<IVirtualActorProvider>();
        actors.GetActor<IProofVerifierActor>(Arg.Any<VirtualActorId>()).Returns(verifier);

        var dispatcher = new AgentVerificationDispatcher(actors, NullLogger<AgentVerificationDispatcher>.Instance);
        var request = CreateRequest();

        using var cts = new CancellationTokenSource();
        var processTask = dispatcher.ProcessAsync(cts.Token);

        await dispatcher.EnqueueAsync(request, TestContext.Current.CancellationToken);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => processTask);

        await verifier.Received(1).VerifyAsync(
            request.WorkspaceId,
            request.AgentName,
            request.TaskId,
            request.Proof);
    }

    [Fact]
    public async Task ProcessAsync_VerifierThrows_SwallowsAndContinues()
    {
        var verifier = Substitute.For<IProofVerifierActor>();
        verifier.VerifyAsync(Arg.Any<WorkspaceId>(), Arg.Any<string>(), Arg.Any<AgentTaskId>(), Arg.Any<ProofOfWork>())
            .Returns(
                _ => Task.FromException(new InvalidOperationException("verifier down")),
                _ => Task.CompletedTask);

        var actors = Substitute.For<IVirtualActorProvider>();
        actors.GetActor<IProofVerifierActor>(Arg.Any<VirtualActorId>()).Returns(verifier);

        var dispatcher = new AgentVerificationDispatcher(actors, NullLogger<AgentVerificationDispatcher>.Instance);

        using var cts = new CancellationTokenSource();
        var processTask = dispatcher.ProcessAsync(cts.Token);

        await dispatcher.EnqueueAsync(CreateRequest(), TestContext.Current.CancellationToken);
        await dispatcher.EnqueueAsync(CreateRequest(), TestContext.Current.CancellationToken);

        await Task.Delay(150, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => processTask);

        await verifier.Received(2).VerifyAsync(
            Arg.Any<WorkspaceId>(),
            Arg.Any<string>(),
            Arg.Any<AgentTaskId>(),
            Arg.Any<ProofOfWork>());
    }
}
