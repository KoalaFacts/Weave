using Weave.Security.Tokens;
using Weave.Shared.Lifecycle;
using Weave.Tools.Connectors;
using Weave.Tools.Tool;

namespace Weave.Tools.Tests;

public sealed partial class ToolActorTests
{
    [Fact]
    public async Task ConnectAsync_ReplacesConnection_DisconnectsPreviousHandle()
    {
        var (actor, connector, tokens) = CreateActor();
        var first = new ToolHandle { ToolName = "tool", Type = ToolType.Cli, ConnectionId = "first", IsConnected = true };
        var second = first with { ConnectionId = "second" };
        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>()).Returns(first, second);
        var spec = new ToolSpec { Name = "tool", Type = ToolType.Cli };
        await actor.ConnectAsync(spec, CreateToken(tokens), TestContext.Current.CancellationToken);
        var restored = await actor.ConnectAsync(spec, CreateToken(tokens), TestContext.Current.CancellationToken);
        restored.ConnectionId.ShouldBe("second");
        (await actor.GetHandleAsync())!.ConnectionId.ShouldBe("second");
        await connector.Received(1).DisconnectAsync(first, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConnectAsync_ReplacementDenied_PreservesExistingConnection()
    {
        var (actor, connector, tokens) = CreateActor();
        var handle = new ToolHandle { ToolName = "tool", Type = ToolType.Cli, ConnectionId = "retained", IsConnected = true };
        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>()).Returns(handle);
        var spec = new ToolSpec { Name = "tool", Type = ToolType.Cli };
        await actor.ConnectAsync(spec, CreateToken(tokens), TestContext.Current.CancellationToken);
        var denied = tokens.Mint(new() { WorkspaceId = "test", IssuedTo = "denied", Grants = ["tool:tool:invoke:run"] });
        await Should.ThrowAsync<UnauthorizedAccessException>(() => actor.ConnectAsync(spec, denied, TestContext.Current.CancellationToken));
        (await actor.GetHandleAsync())!.ConnectionId.ShouldBe("retained");
        await connector.DidNotReceive().DisconnectAsync(Arg.Any<ToolHandle>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisconnectAsync_PostDisconnectHookFails_DoesNotRetainClosedHandle()
    {
        var lifecycle = Substitute.For<ILifecycleManager>();
        var (actor, connector, tokens) = CreateActor(lifecycleOverride: lifecycle);
        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "tool", Type = ToolType.Cli, IsConnected = true });
        await actor.ConnectAsync(new() { Name = "tool", Type = ToolType.Cli }, CreateToken(tokens), TestContext.Current.CancellationToken);
        lifecycle.RunHooksAsync(LifecyclePhase.ToolDisconnected, Arg.Any<LifecycleContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("post-disconnect-hook")));
        await Should.ThrowAsync<InvalidOperationException>(() => actor.DisconnectAsync());
        (await actor.GetHandleAsync()).ShouldBeNull();
        await connector.Received(1).DisconnectAsync(Arg.Any<ToolHandle>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConnectAsync_AuthorityRevokedDuringHook_DeniesBeforeConnector()
    {
        var lifecycle = Substitute.For<ILifecycleManager>();
        var (actor, connector, tokens) = CreateActor(lifecycleOverride: lifecycle);
        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "tool", Type = ToolType.Cli, IsConnected = true });
        var token = CreateToken(tokens);
        lifecycle.RunHooksAsync(LifecyclePhase.ToolConnecting, Arg.Any<LifecycleContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => { tokens.Revoke(token.TokenId); return Task.CompletedTask; });
        await Should.ThrowAsync<UnauthorizedAccessException>(() => actor.ConnectAsync(new() { Name = "tool", Type = ToolType.Cli },
            token, TestContext.Current.CancellationToken));
        await connector.DidNotReceive().ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>());
        (await actor.GetHandleAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task ConnectAsync_ExplicitCancellation_CancelsConnector()
    {
        var (actor, connector, tokens) = CreateActor();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var ct = call.Arg<CancellationToken>();
            entered.SetResult(ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new ToolHandle();
        });
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var connecting = actor.ConnectAsync(new() { Name = "tool", Type = ToolType.Cli }, CreateToken(tokens), caller.Token);
        var dispatched = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        caller.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => connecting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        dispatched.IsCancellationRequested.ShouldBeTrue();
        (await actor.GetHandleAsync()).ShouldBeNull();
    }
}
