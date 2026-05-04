using Microsoft.Extensions.Logging.Abstractions;
using Weave.Security.Actors;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Tools.Tool;
using Weave.Tools.Marketplace;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Tools.Models;

namespace Weave.Tools.Tests;

/// <summary>
/// Covers branches in <see cref="ToolActor"/> not hit by the main fixture:
/// identity-resolution failure modes, tool:* wildcard grants, successful
/// invocation with clean payload that publishes a <c>ToolInvocationCompletedEvent</c>,
/// and schema-when-not-connected.
/// </summary>
public sealed class ToolActorBranchTests
{
    private static CapabilityTokenService CreateTokenService() => new(
        Microsoft.Extensions.Options.Options.Create(
            new CapabilityTokenOptions { SigningKey = "test-signing-key-that-is-at-least-32-chars-long" }),
        TimeProvider.System);

    private sealed class Fixture
    {
        public IVirtualActorProvider ActorProvider { get; } = Substitute.For<IVirtualActorProvider>();
        public IToolDiscoveryService Discovery { get; } = Substitute.For<IToolDiscoveryService>();
        public IToolConnector Connector { get; } = Substitute.For<IToolConnector>();
        public LeakScanner Scanner { get; }
        public CapabilityTokenService TokenService { get; }
        public ILifecycleManager Lifecycle { get; } = Substitute.For<ILifecycleManager>();
        public IEventBus EventBus { get; } = Substitute.For<IEventBus>();
        public ISecretProxyActor SecretProxy { get; } = Substitute.For<ISecretProxyActor>();
        public ToolActor Actor { get; }

        public Fixture()
        {
            Connector.ToolType.Returns(ToolType.Cli);
            Discovery.GetConnector(Arg.Any<ToolType>()).Returns(Connector);
            Scanner = new LeakScanner(NullLogger<LeakScanner>.Instance);
            TokenService = CreateTokenService();
            SecretProxy.SubstituteAsync(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
            ActorProvider.GetActor<ISecretProxyActor>(Arg.Any<VirtualActorId>()).Returns(SecretProxy);

            var authorizer = new CapabilityAuthorizer(TokenService, EventBus, NullLogger<CapabilityAuthorizer>.Instance);
            Actor = new ToolActor(
                ActorProvider, Discovery, Scanner, authorizer, Lifecycle, EventBus,
                NullLogger<ToolActor>.Instance);
        }
    }

    private static CapabilityToken Mint(CapabilityTokenService tokenService, string toolName, bool wildcard = false)
    {
        var grants = new HashSet<string>(StringComparer.Ordinal);
        grants.Add(wildcard ? "tool:*" : $"tool:{toolName}");
        return tokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws-1",
            IssuedTo = "ws-1/agent-1",
            Grants = grants,
            Lifetime = TimeSpan.FromHours(1)
        });
    }

    private static ToolSpec BuildSpec(string toolName = "shell") => new()
    {
        Name = toolName,
        Type = ToolType.Cli
    };

    [Fact]
    public async Task ConnectAsync_WildcardToolGrant_Accepts()
    {
        var fx = new Fixture();
        var token = Mint(fx.TokenService, "shell", wildcard: true);
        fx.Connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle
            {
                ToolName = "shell",
                Type = ToolType.Cli,
                ConnectionId = "id-1",
                IsConnected = true
            });

        var handle = await fx.Actor.ConnectAsync(BuildSpec(), token);

        handle.ToolName.ShouldBe("shell");
    }

    [Fact]
    public async Task GetSchemaAsync_NotConnected_ReturnsNotConnectedDescription()
    {
        var fx = new Fixture();

        var schema = await fx.Actor.GetSchemaAsync();

        schema.Description.ShouldContain("not connected", Case.Insensitive);
    }

    [Fact]
    public async Task InvokeAsync_CleanPayload_PublishesCompletedEvent()
    {
        var fx = new Fixture();
        var token = Mint(fx.TokenService, "shell");
        fx.Connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle
            {
                ToolName = "shell",
                Type = ToolType.Cli,
                ConnectionId = "id-1",
                IsConnected = true
            });
        fx.Connector.InvokeAsync(Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new ToolResult { Success = true, ToolName = "shell", Output = "ok", Duration = TimeSpan.FromMilliseconds(5) });

        await fx.Actor.ConnectAsync(BuildSpec(), token);
        var invocation = new ToolInvocation
        {
            ToolName = "shell",
            Method = "run",
            Parameters = new Dictionary<string, string> { ["cmd"] = "ls" }
        };
        var result = await fx.Actor.InvokeAsync(invocation, token);

        result.Success.ShouldBeTrue();
        await fx.EventBus.Received().PublishAsync(Arg.Any<ToolInvocationCompletedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisconnectAsync_RunsLifecycleHooksAndClearsHandle()
    {
        var fx = new Fixture();
        var token = Mint(fx.TokenService, "shell");
        fx.Connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle
            {
                ToolName = "shell",
                Type = ToolType.Cli,
                ConnectionId = "id-1",
                IsConnected = true
            });
        await fx.Actor.ConnectAsync(BuildSpec(), token);

        await fx.Actor.DisconnectAsync();

        (await fx.Actor.GetHandleAsync()).ShouldBeNull();
        await fx.Lifecycle.Received().RunHooksAsync(
            LifecyclePhase.ToolDisconnecting, Arg.Any<LifecycleContext>(), Arg.Any<CancellationToken>());
        await fx.Lifecycle.Received().RunHooksAsync(
            LifecyclePhase.ToolDisconnected, Arg.Any<LifecycleContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_TokenMissingWorkspaceId_ThrowsInvalidOperationBeforeScanning()
    {
        var fx = new Fixture();
        // Mint a token with empty WorkspaceId — triggers the EnsureIdentity
        // "cannot be established" branch in ToolActor.
        var brokenToken = new CapabilityToken
        {
            WorkspaceId = string.Empty,
            IssuedTo = "caller",
            Grants = ["tool:shell"],
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Signature = "x"
        };
        var invocation = new ToolInvocation { ToolName = "shell", Method = "run", Parameters = [] };

        await Should.ThrowAsync<InvalidOperationException>(
            () => fx.Actor.InvokeAsync(invocation, brokenToken));
    }
}
