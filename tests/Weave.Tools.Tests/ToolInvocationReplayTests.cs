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

public sealed class ToolInvocationReplayTests
{
    [Fact]
    public async Task InvokeAsync_SameIdTwice_DoesNotDispatchTwice()
    {
        var (actor, connector, token) = await CreateConnectedActorAsync();
        var request = Request();

        (await actor.InvokeAsync(request, token)).Success.ShouldBeTrue();
        var duplicate = await actor.InvokeAsync(request, token);

        duplicate.Success.ShouldBeFalse();
        await connector.Received(1).InvokeAsync(Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_ReusedIdWithChangedInput_DoesNotDispatchAgain()
    {
        var (actor, connector, token) = await CreateConnectedActorAsync();
        var request = Request();
        (await actor.InvokeAsync(request, token)).Success.ShouldBeTrue();

        var duplicate = await actor.InvokeAsync(request with { RawInput = "different intent" }, token);

        duplicate.Success.ShouldBeFalse();
        await connector.Received(1).InvokeAsync(Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_DifferentIds_DispatchesBothOperations()
    {
        var (actor, connector, token) = await CreateConnectedActorAsync();
        (await actor.InvokeAsync(Request(), token)).Success.ShouldBeTrue();
        (await actor.InvokeAsync(Request(), token)).Success.ShouldBeTrue();
        await connector.Received(2).InvokeAsync(Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_DeniedRequest_DoesNotConsumeInvocationId()
    {
        var (actor, connector, token) = await CreateConnectedActorAsync();
        var request = Request();
        await Should.ThrowAsync<UnauthorizedAccessException>(() => actor.InvokeAsync(request, token with { Signature = "invalid" }));
        (await actor.InvokeAsync(request, token)).Success.ShouldBeTrue();
        await connector.Received(1).InvokeAsync(Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>());
    }

    private static ToolInvocation Request() => new()
    {
        InvocationId = Guid.NewGuid(),
        ToolName = "files",
        Method = "write_file",
        RawInput = "first intent"
    };

    private static async Task<(ToolActor Actor, IToolConnector Connector, CapabilityToken Token)> CreateConnectedActorAsync()
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        var proxy = Substitute.For<ISecretProxyActor>();
        proxy.SubstituteAsync(Arg.Any<string>()).Returns(call => call.Arg<string>());
        actors.GetActor<ISecretProxyActor>(Arg.Any<VirtualActorId>()).Returns(proxy);
        var connector = Substitute.For<IToolConnector>();
        connector.ToolType.Returns(ToolType.FileSystem);
        connector.NormalizeInvocation(Arg.Any<ToolInvocation>()).Returns(call => call.Arg<ToolInvocation>());
        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "files", Type = ToolType.FileSystem, IsConnected = true, ConnectionId = "test-connection" });
        connector.InvokeAsync(Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new ToolResult { ToolName = "files", Success = true, Output = "done" });
        var discovery = new ToolDiscoveryService([connector], NullLogger<ToolDiscoveryService>.Instance);
        var events = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var tokens = new CapabilityTokenService(Options.Create(new CapabilityTokenOptions
        {
            SigningKey = "test-signing-key-that-is-at-least-32-chars-long"
        }), TimeProvider.System);
        var token = tokens.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "journal-tests",
            IssuedTo = "writer",
            Grants = ["tool:files:connect", "tool:files:invoke:write_file"],
            Lifetime = TimeSpan.FromMinutes(10)
        });
        var actor = new ToolActor(actors, discovery, new LeakScanner(NullLogger<LeakScanner>.Instance),
            new CapabilityAuthorizer(tokens, events, NullLogger<CapabilityAuthorizer>.Instance),
            new LifecycleManager(NullLogger<LifecycleManager>.Instance), events, NullLogger<ToolActor>.Instance);
        await actor.ConnectAsync(new ToolSpec { Name = "files", Type = ToolType.FileSystem }, token);
        return (actor, connector, token);
    }
}
