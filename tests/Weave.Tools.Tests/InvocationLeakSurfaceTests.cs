using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weave.Security.Actors;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Tools.Tool;

namespace Weave.Tools.Tests;

public sealed class InvocationLeakSurfaceTests
{
    private const string SyntheticSecret = "AKIAIOSFODNN7EXAMPLE";
    private const string Redacted = "***REDACTED: potential secret detected in response***";

    [Theory]
    [InlineData(false, SyntheticSecret, "ordinary failure")]
    [InlineData(false, "ordinary output", SyntheticSecret)]
    [InlineData(true, "ordinary output", SyntheticSecret)]
    [InlineData(true, "", SyntheticSecret)]
    [InlineData(false, SyntheticSecret, SyntheticSecret)]
    [InlineData(true, SyntheticSecret, SyntheticSecret)]
    [InlineData(true, SyntheticSecret, "ordinary failure")]
    public async Task InvokeAsync_SecretInResponseField_RedactsOnlyAffectedFields(bool success, string output, string error)
    {
        using var fixture = new Fixture();
        await fixture.ConnectAsync();
        var response = new ToolResult
        {
            ToolName = "files",
            Success = success,
            Output = output,
            Error = error,
            Duration = TimeSpan.FromMilliseconds(42)
        };
        fixture.Connector.InvokeAsync(Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>())
            .Returns(response);

        var result = await fixture.Actor.InvokeAsync(Request(), fixture.Token);

        result.Output.ShouldBe(output == SyntheticSecret ? Redacted : output);
        result.Error.ShouldBe(error == SyntheticSecret ? Redacted : error);
        result.Success.ShouldBe(success);
        result.ToolName.ShouldBe(response.ToolName);
        result.Duration.ShouldBe(response.Duration);
        fixture.Blocked.Count.ShouldBe(1);
        fixture.Blocked.Single().Reason.ShouldBe("Secret leak detected in inbound response");
        await fixture.Connector.Received(1).InvokeAsync(
            Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ordinary input")]
    [InlineData(SyntheticSecret)]
    public async Task InvokeAsync_SecretInParameters_BlocksRegardlessOfRawInput(string? rawInput)
    {
        using var fixture = new Fixture();
        await fixture.ConnectAsync();
        var request = Request() with
        {
            RawInput = rawInput,
            Parameters = new() { ["path"] = SyntheticSecret }
        };

        var result = await fixture.Actor.InvokeAsync(request, fixture.Token);

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe("Tool invocation blocked: potential secret leak detected in payload");
        fixture.Blocked.Count.ShouldBe(1);
        fixture.Blocked.Single().Reason.ShouldBe("Secret leak detected in outbound payload");
        await fixture.Connector.DidNotReceive().InvokeAsync(
            Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>());
        await fixture.Secrets.DidNotReceive().SubstituteAsync(Arg.Any<string>());
    }

    [Theory]
    [InlineData(false, "ordinary output", "file not found")]
    [InlineData(true, "ordinary output", null)]
    [InlineData(false, "", "")]
    public async Task InvokeAsync_CleanResponse_PreservesOutcomeAndFields(bool success, string output, string? error)
    {
        using var fixture = new Fixture();
        await fixture.ConnectAsync();
        fixture.Connector.InvokeAsync(Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new ToolResult { ToolName = "files", Success = success, Output = output, Error = error });

        var result = await fixture.Actor.InvokeAsync(Request(), fixture.Token);

        result.Success.ShouldBe(success);
        result.Output.ShouldBe(output);
        result.Error.ShouldBe(error);
        fixture.Blocked.ShouldBeEmpty();
        await fixture.Connector.Received(1).InvokeAsync(
            Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>());
    }

    private static ToolInvocation Request() => new()
    {
        ToolName = "files",
        Method = "read_file",
        Parameters = new() { ["path"] = "ordinary.txt" },
        RawInput = "ordinary input"
    };

    private sealed class Fixture : IDisposable
    {
        private readonly IDisposable _subscription;
        public IToolConnector Connector { get; } = Substitute.For<IToolConnector>();
        public ISecretProxyActor Secrets { get; } = Substitute.For<ISecretProxyActor>();
        public ConcurrentQueue<ToolInvocationBlockedEvent> Blocked { get; } = new();
        public ToolActor Actor { get; }
        public CapabilityToken Token { get; }

        public Fixture()
        {
            var actors = Substitute.For<IVirtualActorProvider>();
            actors.GetActor<ISecretProxyActor>(Arg.Any<VirtualActorId>()).Returns(Secrets);
            Secrets.SubstituteAsync(Arg.Any<string>()).Returns(call => call.Arg<string>());
            Connector.ToolType.Returns(ToolType.FileSystem);
            Connector.NormalizeInvocation(Arg.Any<ToolInvocation>()).Returns(call => call.Arg<ToolInvocation>());
            Connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
                .Returns(new ToolHandle { ToolName = "files", Type = ToolType.FileSystem, IsConnected = true });
            Connector.InvokeAsync(Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>())
                .Returns(new ToolResult { ToolName = "files", Success = true, Output = "ordinary output" });
            var discovery = new ToolDiscoveryService([Connector], NullLogger<ToolDiscoveryService>.Instance);
            var events = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
            _subscription = events.Subscribe<ToolInvocationBlockedEvent>((evt, _) =>
            {
                Blocked.Enqueue(evt);
                return Task.CompletedTask;
            });
            var tokens = new CapabilityTokenService(Options.Create(new CapabilityTokenOptions
            {
                SigningKey = "test-signing-key-that-is-at-least-32-chars-long"
            }), TimeProvider.System);
            Token = tokens.Mint(new CapabilityTokenRequest
            {
                WorkspaceId = "leak-surface-tests",
                IssuedTo = "reader",
                Grants = ["tool:files:connect", "tool:files:invoke:read_file"],
                Lifetime = TimeSpan.FromMinutes(10)
            });
            // The journal now owns elapsed duration. Keep the exact 42ms assertion deterministic.
            var clock = Substitute.For<TimeProvider>();
            clock.GetUtcNow().Returns(new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero));
            clock.TimestampFrequency.Returns(TimeSpan.TicksPerSecond);
            clock.GetTimestamp().Returns(0L, TimeSpan.FromMilliseconds(42).Ticks);
            Actor = new ToolActor(actors, discovery, new LeakScanner(NullLogger<LeakScanner>.Instance),
                new CapabilityAuthorizer(tokens, events, NullLogger<CapabilityAuthorizer>.Instance),
                new LifecycleManager(NullLogger<LifecycleManager>.Instance), events, NullLogger<ToolActor>.Instance,
                new TestInvocationJournal(), clock, TestFileWriteApprovals.Create());
        }

        public Task<ToolHandle> ConnectAsync() => Actor.ConnectAsync(
            new ToolSpec { Name = "files", Type = ToolType.FileSystem }, Token);

        public void Dispose() => _subscription.Dispose();
    }
}
