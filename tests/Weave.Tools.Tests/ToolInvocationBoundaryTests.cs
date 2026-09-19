using Microsoft.Extensions.Logging.Abstractions;
using Weave.Security.Actors;
using Weave.Security.Events;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Tools.Tool;

namespace Weave.Tools.Tests;

public sealed class ToolInvocationBoundaryTests
{
    [Theory]
    [InlineData("other")]
    [InlineData("Notes")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task ConnectAsync_TargetDiffersFromActivatedActor_RejectsBeforeConnecting(string requestedName)
    {
        var fx = new Fixture();
        await fx.Actor.OnActivatedAsync("ws/notes", TestContext.Current.CancellationToken);

        await Should.ThrowAsync<UnauthorizedAccessException>(() => fx.Actor.ConnectAsync(
            new ToolSpec { Name = requestedName, Type = ToolType.FileSystem }, fx.Token));

        await fx.Connector.DidNotReceive().ConnectAsync(
            Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>());
        (await fx.Actor.GetHandleAsync()).ShouldBeNull();
    }

    [Theory]
    [InlineData("other")]
    [InlineData("Notes")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task InvokeAsync_TargetDiffersFromConnectedActor_RejectsBeforeDispatch(string requestedName)
    {
        var fx = new Fixture();
        await fx.ConnectAsync();

        await Should.ThrowAsync<UnauthorizedAccessException>(() => fx.Actor.InvokeAsync(
            new ToolInvocation { ToolName = requestedName, Method = "read_file" }, fx.Token));

        await fx.Connector.DidNotReceive().InvokeAsync(
            Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>());
        await fx.Secrets.DidNotReceive().SubstituteAsync(Arg.Any<string>());
        await fx.Events.DidNotReceive().PublishAsync(
            Arg.Any<ToolInvocationCompletedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConnectAsync_RejectedRebind_PreservesExistingConnection()
    {
        var fx = new Fixture();
        await fx.ConnectAsync();
        var original = await fx.Actor.GetHandleAsync();

        await Should.ThrowAsync<UnauthorizedAccessException>(() => fx.Actor.ConnectAsync(
            new ToolSpec { Name = "other", Type = ToolType.FileSystem }, fx.Token));

        (await fx.Actor.GetHandleAsync()).ShouldBeSameAs(original);
        var result = await fx.Actor.InvokeAsync(new ToolInvocation
        {
            ToolName = "notes",
            Method = "read_file",
            Parameters = new() { ["path"] = "original.txt" }
        }, fx.Token);
        result.Success.ShouldBeTrue();
        result.Output.ShouldBe("original.txt");
        await fx.Connector.Received(1).ConnectAsync(
            Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_ParametersChangeDuringAuthorization_ExecutesOriginalSnapshot()
    {
        var fx = new Fixture();
        await fx.ConnectAsync();
        var input = new ToolInvocation
        {
            ToolName = "notes",
            Method = "read_file",
            Parameters = new() { ["path"] = "original.txt" }
        };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.Events.PublishAsync(Arg.Any<CapabilityAuthorizationEvent>(), Arg.Any<CancellationToken>())
            .Returns(release.Task);

        var pending = fx.Actor.InvokeAsync(input, fx.Token);
        var wasPending = !pending.IsCompleted;
        input.Parameters["path"] = "changed.txt";
        input.Parameters["extra"] = "unapproved";
        release.SetResult();
        var result = await pending.WaitAsync(TestContext.Current.CancellationToken);

        wasPending.ShouldBeTrue();
        result.Success.ShouldBeTrue();
        result.Output.ShouldBe("original.txt");
        await fx.Connector.Received(1).InvokeAsync(Arg.Any<ToolHandle>(),
            Arg.Is<ToolInvocation>(i => i.Method == "read_file" && i.Parameters.Count == 1
                && i.Parameters["path"] == "original.txt"), Arg.Any<CancellationToken>());
        input.Parameters["path"].ShouldBe("changed.txt");
    }

    [Fact]
    public async Task InvokeAsync_LeakingInputChangedDuringAuthorization_RemainsBlocked()
    {
        var fx = new Fixture();
        await fx.ConnectAsync();
        var input = new ToolInvocation
        {
            ToolName = "notes",
            Method = "write_file",
            Parameters = new() { ["path"] = "AKIAIOSFODNN7EXAMPLE" }
        };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.Events.PublishAsync(Arg.Any<CapabilityAuthorizationEvent>(), Arg.Any<CancellationToken>())
            .Returns(release.Task);

        var pending = fx.Actor.InvokeAsync(input, fx.Token);
        input.Parameters["path"] = "clean.txt";
        release.SetResult();
        var result = await pending.WaitAsync(TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("secret leak");
        await fx.Connector.DidNotReceive().InvokeAsync(
            Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_ValidTarget_PreservesSubstitutionAndResponseRedaction()
    {
        var fx = new Fixture();
        await fx.ConnectAsync();
        fx.Secrets.SubstituteAsync("${secrets.note_path}").Returns("resolved.txt");
        fx.Connector.InvokeAsync(Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new ToolResult { ToolName = "notes", Success = true, Output = "AKIAIOSFODNN7EXAMPLE" });
        var input = new ToolInvocation
        {
            ToolName = "notes",
            Method = "read_file",
            Parameters = new() { ["path"] = "${secrets.note_path}" }
        };

        var result = await fx.Actor.InvokeAsync(input, fx.Token);

        result.Success.ShouldBeTrue();
        result.Output.ShouldBe("***REDACTED: potential secret detected in response***");
        await fx.Connector.Received(1).InvokeAsync(Arg.Any<ToolHandle>(),
            Arg.Is<ToolInvocation>(i => i.Parameters["path"] == "resolved.txt"), Arg.Any<CancellationToken>());
        input.Parameters["path"].ShouldBe("${secrets.note_path}");
    }

    [Theory]
    [InlineData("other-workspace", false)]
    [InlineData("ws", true)]
    public async Task InvokeAsync_InvalidAuthority_DoesNotDispatch(string workspaceId, bool tamper)
    {
        var fx = new Fixture();
        await fx.ConnectAsync();
        var token = fx.Mint(workspaceId);
        if (tamper)
            token = token with { Signature = "invalid-signature" };

        await Should.ThrowAsync<UnauthorizedAccessException>(() => fx.Actor.InvokeAsync(
            new ToolInvocation { ToolName = "notes", Method = "read_file" }, token));

        await fx.Connector.DidNotReceive().InvokeAsync(
            Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>());
    }

    private sealed class Fixture
    {
        public IToolConnector Connector { get; } = Substitute.For<IToolConnector>();
        public ISecretProxyActor Secrets { get; } = Substitute.For<ISecretProxyActor>();
        public IEventBus Events { get; } = Substitute.For<IEventBus>();
        public CapabilityTokenService Tokens { get; } = new(
            Microsoft.Extensions.Options.Options.Create(new CapabilityTokenOptions
            {
                SigningKey = "test-signing-key-that-is-at-least-32-chars-long"
            }), TimeProvider.System);
        public CapabilityToken Token { get; }
        public ToolActor Actor { get; }

        public Fixture()
        {
            var actors = Substitute.For<IVirtualActorProvider>();
            actors.GetActor<ISecretProxyActor>(Arg.Any<VirtualActorId>()).Returns(Secrets);
            Secrets.SubstituteAsync(Arg.Any<string>()).Returns(c => c.Arg<string>());
            var discovery = Substitute.For<IToolDiscoveryService>();
            discovery.GetConnector(ToolType.FileSystem).Returns(Connector);
            Connector.NormalizeInvocation(Arg.Any<ToolInvocation>()).Returns(ci => ci.Arg<ToolInvocation>());
            Connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
                .Returns(new ToolHandle { ToolName = "notes", Type = ToolType.FileSystem, IsConnected = true });
            Connector.InvokeAsync(Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>())
                .Returns(c => new ToolResult
                {
                    ToolName = "notes",
                    Success = true,
                    Output = c.Arg<ToolInvocation>().Parameters.GetValueOrDefault("path", "no-path")
                });
            Token = Mint("ws");
            Actor = new ToolActor(actors, discovery, new LeakScanner(NullLogger<LeakScanner>.Instance),
                new CapabilityAuthorizer(Tokens, Events, NullLogger<CapabilityAuthorizer>.Instance),
                Substitute.For<ILifecycleManager>(), Events, NullLogger<ToolActor>.Instance);
        }

        public CapabilityToken Mint(string workspaceId) => Tokens.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = workspaceId,
            IssuedTo = "test-agent",
            Grants = ["tool:notes:connect", "tool:notes:invoke:read_file", "tool:notes:invoke:write_file"],
            Lifetime = TimeSpan.FromHours(1)
        });

        public async Task ConnectAsync()
        {
            await Actor.OnActivatedAsync("ws/notes", TestContext.Current.CancellationToken);
            await Actor.ConnectAsync(new ToolSpec { Name = "notes", Type = ToolType.FileSystem }, Token);
        }
    }
}
