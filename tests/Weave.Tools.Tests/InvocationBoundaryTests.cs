using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weave.Invocations;
using Weave.Security.Actors;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Tools.Tool;

namespace Weave.Tools.Tests;

public sealed class InvocationBoundaryTests
{
    [Fact]
    public async Task InvokeAsync_IdLetterCaseDoesNotCreateASecondLogicalInvocation()
    {
        using var fx = new Fixture();
        await fx.ConnectAsync();
        var request = Request() with { InvocationId = InvocationId.From("abcdef0123456789abcdef0123456789") };
        (await fx.Actor.InvokeAsync(request, fx.Token)).Success.ShouldBeTrue();
        var replay = await fx.Actor.InvokeAsync(request with
        {
            InvocationId = InvocationId.From("ABCDEF0123456789ABCDEF0123456789")
        }, fx.Token);
        replay.IsReplay.ShouldBeTrue();
        fx.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task GetInvocationAsync_RevokedDuringJournalRead_DoesNotDiscloseRecord()
    {
        using var fx = new Fixture();
        await fx.ConnectAsync();
        var request = Request();
        (await fx.Actor.InvokeAsync(request, fx.Token)).Success.ShouldBeTrue();
        fx.Journal.AfterFind = () => fx.Tokens.Revoke(fx.Token.TokenId);

        await Should.ThrowAsync<UnauthorizedAccessException>(() => fx.Actor.GetInvocationAsync(request.InvocationId!.Value, fx.Token));
    }

    [Fact]
    public async Task InvokeAsync_CompletionNotificationFails_ReturnsDurablyRecordedOutcome()
    {
        using var fx = new Fixture();
        await fx.ConnectAsync();
        fx.Events.PublishAsync(Arg.Any<ToolInvocationCompletedEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("private subscriber detail")));
        var result = await fx.Actor.InvokeAsync(Request(), fx.Token);
        result.Success.ShouldBeTrue();
        result.OutcomeRecorded.ShouldBeTrue();
        fx.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task InvokeAsync_ParameterOrderDoesNotChangeTheLogicalInput()
    {
        using var fx = new Fixture();
        await fx.ConnectAsync();
        var request = Request() with { Parameters = new() { ["a"] = "first", ["b"] = "second" } };
        (await fx.Actor.InvokeAsync(request, fx.Token)).Success.ShouldBeTrue();
        var replay = await fx.Actor.InvokeAsync(request with
        {
            Parameters = new() { ["b"] = "second", ["a"] = "first" }
        }, fx.Token);
        replay.IsReplay.ShouldBeTrue();
        fx.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task InvokeAsync_NullAndEmptyRawInput_AreDifferentRequests()
    {
        using var fx = new Fixture();
        await fx.ConnectAsync();
        var request = Request() with { RawInput = null };
        (await fx.Actor.InvokeAsync(request, fx.Token)).Success.ShouldBeTrue();
        var conflict = await fx.Actor.InvokeAsync(request with { RawInput = string.Empty }, fx.Token);
        conflict.ErrorCode.ShouldBe("invocation-id-conflict");
        fx.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../not-an-id")]
    [InlineData("00000000000000000000000000000000")]
    public async Task InvokeAsync_InvalidIdentity_DoesNotDispatch(string id)
    {
        using var fx = new Fixture();
        await fx.ConnectAsync();
        var result = await fx.Actor.InvokeAsync(Request() with { InvocationId = InvocationId.From(id) }, fx.Token);
        result.Outcome.ShouldBe(InvocationOutcome.NotDispatched);
        result.ErrorCode.ShouldBe("invalid-invocation");
        fx.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task InvokeAsync_NoCallerId_ReturnsDistinctIdsForDistinctCalls()
    {
        using var fx = new Fixture();
        await fx.ConnectAsync();
        var request = Request() with { InvocationId = null };
        var first = await fx.Actor.InvokeAsync(request, fx.Token);
        var second = await fx.Actor.InvokeAsync(request, fx.Token);
        first.InvocationId.ShouldNotBeNull();
        second.InvocationId.ShouldNotBeNull();
        first.InvocationId.ShouldNotBe(second.InvocationId);
        fx.Calls.ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_RevokedOrCancelledAfterAdmission_BlocksConnector(bool cancel)
    {
        using var fx = new Fixture();
        using var caller = new CancellationTokenSource();
        await fx.ConnectAsync();
        var request = Request();
        var token = fx.Token with { CancellationToken = caller.Token };
        fx.Journal.AfterClaim = () =>
        {
            if (cancel)
                caller.Cancel();
            else
                fx.Tokens.Revoke(token.TokenId);
        };
        if (cancel)
            await Should.ThrowAsync<OperationCanceledException>(() => fx.Actor.InvokeAsync(request, token));
        else
            await Should.ThrowAsync<UnauthorizedAccessException>(() => fx.Actor.InvokeAsync(request, token));
        fx.Calls.ShouldBe(0);
        fx.Journal.Find("ws", request.InvocationId!.Value, TestContext.Current.CancellationToken)
            .ShouldNotBeNull().Attempt.Outcome.ShouldBe(cancel ? InvocationOutcome.Cancelled : InvocationOutcome.Denied);
    }

    [Fact]
    public async Task InvokeAsync_DuplicateWhileFirstIsInFlight_DoesNotDispatchTwice()
    {
        using var fx = new Fixture();
        await fx.ConnectAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.Connector.InvokeAsync(Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                entered.TrySetResult();
                return release.Task;
            });
        var request = Request();
        var first = fx.Actor.InvokeAsync(request, fx.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var replay = await fx.Actor.InvokeAsync(request, fx.Token);
            replay.IsReplay.ShouldBeTrue();
            replay.Outcome.ShouldBe(InvocationOutcome.OutcomeUnknown);
            await fx.Connector.Received(1).InvokeAsync(Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            release.TrySetResult(new ToolResult { Success = true });
            await first;
        }
    }

    private static ToolInvocation Request() => new()
    {
        InvocationId = InvocationId.From(Guid.NewGuid().ToString("N")),
        ToolName = "files",
        Method = "write_file",
        RawInput = "safe body",
        Parameters = new() { ["path"] = "note.txt" }
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"weave-invocation-boundary-{Guid.NewGuid():N}");
        public CapabilityTokenService Tokens { get; }
        public CapabilityToken Token { get; }
        public ToolActor Actor { get; }
        public InterceptingJournal Journal { get; } = new();
        public IEventBus Events { get; } = Substitute.For<IEventBus>();
        public IToolConnector Connector { get; } = Substitute.For<IToolConnector>();
        public int Calls { get; private set; }

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Tokens = new CapabilityTokenService(Options.Create(new CapabilityTokenOptions
            {
                SigningKey = "test-signing-key-that-is-at-least-32-chars-long",
                RevocationDirectory = Path.Combine(_root, "revocations")
            }), TimeProvider.System);
            Token = Tokens.Mint(new CapabilityTokenRequest
            {
                WorkspaceId = "ws", IssuedTo = "writer",
                Grants = ["tool:files:connect", "tool:files:invoke:write_file", "invocation:read"],
                Lifetime = TimeSpan.FromMinutes(5)
            });
            var actors = Substitute.For<IVirtualActorProvider>();
            var secret = Substitute.For<ISecretProxyActor>();
            secret.SubstituteAsync(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
            actors.GetActor<ISecretProxyActor>(Arg.Any<VirtualActorId>()).Returns(secret);
            Connector.NormalizeInvocation(Arg.Any<ToolInvocation>()).Returns(ci => ci.Arg<ToolInvocation>());
            Connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
                .Returns(new ToolHandle { ToolName = "files", Type = ToolType.FileSystem, IsConnected = true });
            Connector.InvokeAsync(Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    Calls++;
                    return new ToolResult { Success = true, Output = "result" };
                });
            var discovery = Substitute.For<IToolDiscoveryService>();
            discovery.GetConnector(ToolType.FileSystem).Returns(Connector);
            Actor = new ToolActor(actors, discovery, new LeakScanner(NullLogger<LeakScanner>.Instance),
                new CapabilityAuthorizer(Tokens, Events, NullLogger<CapabilityAuthorizer>.Instance),
                new LifecycleManager(NullLogger<LifecycleManager>.Instance), Events, NullLogger<ToolActor>.Instance,
                Journal, TimeProvider.System);
        }

        public Task<ToolHandle> ConnectAsync() => Actor.ConnectAsync(new ToolSpec { Name = "files", Type = ToolType.FileSystem }, Token);
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class InterceptingJournal : IInvocationJournal
    {
        private readonly TestInvocationJournal _inner = new();
        public Action? AfterClaim { get; set; }
        public Action? AfterFind { get; set; }
        public InvocationClaim TryStart(InvocationRecord candidate, CancellationToken cancellationToken)
        {
            var claim = _inner.TryStart(candidate, cancellationToken);
            AfterClaim?.Invoke();
            return claim;
        }
        public InvocationRecord? Find(string workspaceId, InvocationId invocationId, CancellationToken cancellationToken)
        {
            var record = _inner.Find(workspaceId, invocationId, cancellationToken);
            AfterFind?.Invoke();
            return record;
        }
        public bool Complete(string workspaceId, InvocationId invocationId, InvocationAttemptId attemptId,
            InvocationOutcome outcome, DateTimeOffset completedAt, TimeSpan duration) =>
            _inner.Complete(workspaceId, invocationId, attemptId, outcome, completedAt, duration);
    }
}
