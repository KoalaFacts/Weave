using Microsoft.Extensions.Logging.Abstractions;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Tools.Tool;

namespace Weave.Tools.Tests;

public sealed partial class ToolActorTests
{
    [Fact]
    public async Task HasCurrentConnectionAsync_ConnectedThenDisconnected_TracksLiveHandle()
    {
        var (actor, connector, tokens) = CreateActor();
        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "tool", Type = ToolType.Cli, IsConnected = true });
        (await actor.HasCurrentConnectionAsync(ToolType.Cli, null, TestContext.Current.CancellationToken)).ShouldBeFalse();
        await actor.ConnectAsync(new() { Name = "tool", Type = ToolType.Cli }, CreateToken(tokens), TestContext.Current.CancellationToken);
        (await actor.HasCurrentConnectionAsync(ToolType.Cli, null, TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await actor.HasCurrentConnectionAsync(ToolType.Mcp, null, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await actor.HasCurrentConnectionAsync(ToolType.Cli, "ws/other", TestContext.Current.CancellationToken)).ShouldBeFalse();
        await actor.DisconnectAsync();
        (await actor.HasCurrentConnectionAsync(ToolType.Cli, null, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HasCurrentConnectionAsync_ConnectorReplacedOrRemoved_ReturnsFalse(bool removed)
    {
        var discovery = Substitute.For<IToolDiscoveryService>();
        var connector = Substitute.For<IToolConnector>();
        discovery.GetConnector(ToolType.Mcp, "ws/server").Returns(connector);
        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "echo", Type = ToolType.Mcp, IsConnected = true });
        var actor = new ToolActor(Substitute.For<IVirtualActorProvider>(), discovery, Substitute.For<ILeakScanner>(),
            Substitute.For<ICapabilityAuthorizer>(), Substitute.For<ILifecycleManager>(), Substitute.For<IEventBus>(),
            NullLogger<ToolActor>.Instance, new TestInvocationJournal(), TimeProvider.System);
        await actor.OnActivatedAsync("ws/echo", TestContext.Current.CancellationToken);
        await actor.ConnectAsync(new() { Name = "echo", Type = ToolType.Mcp, InstallationId = "ws/server" },
            new() { WorkspaceId = "ws" }, TestContext.Current.CancellationToken);
        (await actor.HasCurrentConnectionAsync(ToolType.Mcp, "ws/server", TestContext.Current.CancellationToken)).ShouldBeTrue();
        if (removed)
            discovery.GetConnector(ToolType.Mcp, "ws/server").Returns(_ => throw new NotSupportedException());
        else
            discovery.GetConnector(ToolType.Mcp, "ws/server").Returns(Substitute.For<IToolConnector>());
        (await actor.HasCurrentConnectionAsync(ToolType.Mcp, "ws/server", TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await actor.GetHandleAsync()).ShouldNotBeNull();
    }

    [Fact]
    public async Task HasCurrentConnectionAsync_Cancelled_Throws()
    {
        var (actor, _, _) = CreateActor();
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => actor.HasCurrentConnectionAsync(ToolType.Cli, null, caller.Token));
    }
}
