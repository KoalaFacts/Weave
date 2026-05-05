using Microsoft.Extensions.Logging;
using Weave.Security.Actors;
using Weave.Security.Events;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Tools.Marketplace;
using Weave.Tools.Tool;

namespace Weave.Tools.Tests;

public sealed class ToolActorTests
{
    private static (ToolActor Actor, IToolConnector Connector, ICapabilityTokenService TokenService) CreateActor(IEventBus? eventBusOverride = null)
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        var connector = Substitute.For<IToolConnector>();
        connector.ToolType.Returns(ToolType.Cli);

        var discovery = Substitute.For<IToolDiscoveryService>();
        discovery.GetConnector(ToolType.Cli).Returns(connector);

        var leakScanner = new LeakScanner(Substitute.For<ILogger<LeakScanner>>());
        var tokenService = new CapabilityTokenService(
            Microsoft.Extensions.Options.Options.Create(
                new CapabilityTokenOptions { SigningKey = "test-signing-key-that-is-at-least-32-chars-long" }),
            TimeProvider.System);
        var lifecycleManager = Substitute.For<ILifecycleManager>();
        var logger = Substitute.For<ILogger<ToolActor>>();
        var eventBus = eventBusOverride ?? Substitute.For<IEventBus>();
        var authorizer = new CapabilityAuthorizer(tokenService, eventBus, Microsoft.Extensions.Logging.Abstractions.NullLogger<CapabilityAuthorizer>.Instance);
        var secretProxy = Substitute.For<ISecretProxyActor>();
        secretProxy.SubstituteAsync(Arg.Any<string>()).Returns(callInfo => callInfo.Arg<string>());

        actors.GetActor<ISecretProxyActor>(Arg.Any<VirtualActorId>()).Returns(secretProxy);

        var actor = new ToolActor(actors, discovery, leakScanner, authorizer, lifecycleManager, eventBus, logger);
        return (actor, connector, tokenService);
    }

    private static CapabilityToken CreateToken(ICapabilityTokenService svc, string workspaceId = "test") =>
        svc.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = workspaceId,
            IssuedTo = "agent",
            Grants = ["tool:*", "secret:*"],
            Lifetime = TimeSpan.FromHours(1)
        });

    [Fact]
    public async Task ConnectAsync_WithValidToken_ReturnsHandle()
    {
        var (actor, connector, tokenSvc) = CreateActor();
        var token = CreateToken(tokenSvc);

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "test-tool", Type = ToolType.Cli, IsConnected = true });

        var definition = new ToolSpec { Name = "test-tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Manifest.CliConfig() };
        var handle = await actor.ConnectAsync(definition, token);

        handle.ShouldNotBeNull();
        handle.ToolName.ShouldBe("test-tool");
        handle.IsConnected.ShouldBeTrue();
    }

    [Fact]
    public async Task ConnectAsync_WithInvalidToken_Throws()
    {
        var (actor, _, tokenSvc) = CreateActor();
        var token = tokenSvc.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "test",
            IssuedTo = "agent",
            Grants = ["*"],
            Lifetime = TimeSpan.FromMilliseconds(-1)
        });

        var definition = new ToolSpec { Name = "tool", Type = ToolType.Cli };
        await Should.ThrowAsync<UnauthorizedAccessException>(() => actor.ConnectAsync(definition, token));
    }

    [Fact]
    public async Task InvokeAsync_WithSecretInPayload_BlocksInvocation()
    {
        var (actor, connector, tokenSvc) = CreateActor();
        var token = CreateToken(tokenSvc);

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "tool", Type = ToolType.Cli, IsConnected = true });

        var definition = new ToolSpec { Name = "tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Manifest.CliConfig() };
        await actor.ConnectAsync(definition, token);

        var invocation = new ToolInvocation
        {
            ToolName = "tool",
            Method = "exec",
            RawInput = "AKIAIOSFODNN7EXAMPLE"
        };

        var result = await actor.InvokeAsync(invocation, token);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("secret leak");
    }

    // --- DisconnectAsync ---

    [Fact]
    public async Task DisconnectAsync_WhenConnected_CallsConnectorDisconnect()
    {
        var (actor, connector, tokenSvc) = CreateActor();
        var token = CreateToken(tokenSvc);

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "tool", Type = ToolType.Cli, IsConnected = true });

        var definition = new ToolSpec { Name = "tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Manifest.CliConfig() };
        await actor.ConnectAsync(definition, token);

        await actor.DisconnectAsync();

        await connector.Received(1).DisconnectAsync(Arg.Any<ToolHandle>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisconnectAsync_WhenNotConnected_DoesNotThrow()
    {
        var (actor, connector, _) = CreateActor();

        // Disconnect without ever connecting — should be a no-op
        await actor.DisconnectAsync();

        await connector.DidNotReceive().DisconnectAsync(Arg.Any<ToolHandle>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisconnectAsync_AfterDisconnect_HandleIsNull()
    {
        var (actor, connector, tokenSvc) = CreateActor();
        var token = CreateToken(tokenSvc);

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "tool", Type = ToolType.Cli, IsConnected = true });

        await actor.ConnectAsync(new ToolSpec { Name = "tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Manifest.CliConfig() }, token);
        await actor.DisconnectAsync();

        var handle = await actor.GetHandleAsync();
        handle.ShouldBeNull();
    }

    // --- GetHandleAsync ---

    [Fact]
    public async Task GetHandleAsync_WhenNotConnected_ReturnsNull()
    {
        var (actor, _, _) = CreateActor();

        var handle = await actor.GetHandleAsync();

        handle.ShouldBeNull();
    }

    [Fact]
    public async Task GetHandleAsync_WhenConnected_ReturnsHandle()
    {
        var (actor, connector, tokenSvc) = CreateActor();
        var token = CreateToken(tokenSvc);

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "tool", Type = ToolType.Cli, ConnectionId = "cli:tool:abc", IsConnected = true });

        await actor.ConnectAsync(new ToolSpec { Name = "tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Manifest.CliConfig() }, token);

        var handle = await actor.GetHandleAsync();
        handle.ShouldNotBeNull();
        handle!.IsConnected.ShouldBeTrue();
    }

    // --- GetSchemaAsync ---

    [Fact]
    public async Task GetSchemaAsync_WhenNotConnected_ReturnsNotConnectedDescription()
    {
        var (actor, _, _) = CreateActor();

        var schema = await actor.GetSchemaAsync();

        schema.Description.ShouldContain("not connected");
    }

    [Fact]
    public async Task GetSchemaAsync_WhenConnected_DelegatesToConnector()
    {
        var (actor, connector, tokenSvc) = CreateActor();
        var token = CreateToken(tokenSvc);

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "tool", Type = ToolType.Cli, IsConnected = true });
        connector.DiscoverSchemaAsync(Arg.Any<ToolHandle>(), Arg.Any<CancellationToken>())
            .Returns(new ToolSchema { ToolName = "tool", Description = "A test CLI tool" });

        await actor.ConnectAsync(new ToolSpec { Name = "tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Manifest.CliConfig() }, token);

        var schema = await actor.GetSchemaAsync();
        schema.Description.ShouldBe("A test CLI tool");
    }

    // --- InvokeAsync: not connected ---

    [Fact]
    public async Task InvokeAsync_NotConnected_Throws()
    {
        var (actor, _, tokenSvc) = CreateActor();
        var token = CreateToken(tokenSvc);
        var invocation = new ToolInvocation { ToolName = "tool", Method = "run", Parameters = [] };

        await Should.ThrowAsync<InvalidOperationException>(() => actor.InvokeAsync(invocation, token));
    }

    // --- InvokeAsync: invalid token ---

    [Fact]
    public async Task InvokeAsync_InvalidToken_Throws()
    {
        var (actor, connector, tokenSvc) = CreateActor();
        var token = CreateToken(tokenSvc);

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "tool", Type = ToolType.Cli, IsConnected = true });

        await actor.ConnectAsync(new ToolSpec { Name = "tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Manifest.CliConfig() }, token);

        var expired = token with { Signature = "tampered" };
        var invocation = new ToolInvocation { ToolName = "tool", Method = "run", Parameters = [] };

        await Should.ThrowAsync<UnauthorizedAccessException>(() => actor.InvokeAsync(invocation, expired));
    }

    // --- InvokeAsync: success path ---

    [Fact]
    public async Task InvokeAsync_CleanPayload_ReturnsResult()
    {
        var (actor, connector, tokenSvc) = CreateActor();
        var token = CreateToken(tokenSvc);

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "tool", Type = ToolType.Cli, IsConnected = true });
        connector.InvokeAsync(Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new ToolResult { Success = true, ToolName = "tool", Output = "hello world" });

        await actor.ConnectAsync(new ToolSpec { Name = "tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Manifest.CliConfig() }, token);

        var invocation = new ToolInvocation { ToolName = "tool", Method = "echo", RawInput = "safe input", Parameters = [] };
        var result = await actor.InvokeAsync(invocation, token);

        result.Success.ShouldBeTrue();
        result.Output.ShouldBe("hello world");
    }

    // --- InvokeAsync: response leak redaction ---

    [Fact]
    public async Task InvokeAsync_LeakInResponse_RedactsOutput()
    {
        var (actor, connector, tokenSvc) = CreateActor();
        var token = CreateToken(tokenSvc);

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "tool", Type = ToolType.Cli, IsConnected = true });
        connector.InvokeAsync(Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new ToolResult { Success = true, ToolName = "tool", Output = "result: AKIAIOSFODNN7EXAMPLE" });

        await actor.ConnectAsync(new ToolSpec { Name = "tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Manifest.CliConfig() }, token);

        var invocation = new ToolInvocation { ToolName = "tool", Method = "fetch", RawInput = "safe", Parameters = [] };
        var result = await actor.InvokeAsync(invocation, token);

        result.Output.ShouldContain("REDACTED");
        result.Output.ShouldNotContain("AKIAIOSFODNN7EXAMPLE");
    }

    // --- InvokeAsync: failed result with clean output skips response scan ---

    [Fact]
    public async Task InvokeAsync_FailedResult_DoesNotScanResponse()
    {
        var (actor, connector, tokenSvc) = CreateActor();
        var token = CreateToken(tokenSvc);

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "tool", Type = ToolType.Cli, IsConnected = true });
        connector.InvokeAsync(Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new ToolResult { Success = false, ToolName = "tool", Output = "AKIAIOSFODNN7EXAMPLE", Error = "process failed" });

        await actor.ConnectAsync(new ToolSpec { Name = "tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Manifest.CliConfig() }, token);

        var invocation = new ToolInvocation { ToolName = "tool", Method = "run", RawInput = "safe", Parameters = [] };
        var result = await actor.InvokeAsync(invocation, token);

        // Failed results skip response scanning — the leak in output is NOT redacted
        result.Success.ShouldBeFalse();
        result.Output.ShouldContain("AKIAIOSFODNN7EXAMPLE");
    }

    // --- ConnectAsync: token without tool grant ---

    [Fact]
    public async Task ConnectAsync_TokenWithoutToolGrant_Throws()
    {
        var (actor, _, tokenSvc) = CreateActor();
        var token = tokenSvc.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "test",
            IssuedTo = "agent",
            Grants = ["secret:*"],  // no tool:* grant
            Lifetime = TimeSpan.FromHours(1)
        });

        var definition = new ToolSpec { Name = "tool", Type = ToolType.Cli };
        await Should.ThrowAsync<UnauthorizedAccessException>(() => actor.ConnectAsync(definition, token));
    }

    // --- OnActivatedAsync ---

    [Fact]
    public async Task OnActivatedAsync_WithSlashKey_SplitsWorkspaceAndTool()
    {
        var (actor, connector, tokenSvc) = CreateActor();
        await actor.OnActivatedAsync("my-ws/my-tool", TestContext.Current.CancellationToken);

        // After activation with workspace/tool key, connect should work
        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "my-tool", Type = ToolType.Cli, IsConnected = true });

        var token = CreateToken(tokenSvc, workspaceId: "my-ws");
        var spec = new ToolSpec { Name = "my-tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Manifest.CliConfig() };
        var handle = await actor.ConnectAsync(spec, token);
        handle.ToolName.ShouldBe("my-tool");
    }

    [Fact]
    public async Task OnActivatedAsync_WithSimpleKey_UsesAsWorkspaceAndTool()
    {
        var (actor, _, _) = CreateActor();
        await actor.OnActivatedAsync("simple-key", TestContext.Current.CancellationToken);

        // No slash — workspace = key, tool = key
        var schema = await actor.GetSchemaAsync();
        schema.ShouldNotBeNull();
    }

    [Fact]
    public async Task OnActivatedAsync_NullKey_DoesNotThrow()
    {
        var (actor, _, _) = CreateActor();
        await actor.OnActivatedAsync(null, TestContext.Current.CancellationToken);
    }

    // --- GetSchemaAsync ---

    [Fact]
    public async Task GetSchemaAsync_NotConnected_ReturnsDefaultSchema()
    {
        var (actor, _, _) = CreateActor();
        await actor.OnActivatedAsync("ws/tool", TestContext.Current.CancellationToken);

        var schema = await actor.GetSchemaAsync();
        schema.ShouldNotBeNull();
        schema.ToolName.ShouldBe("tool");
        schema.Description.ShouldBe("Tool not connected");
    }

    // --- GetHandleAsync ---

    [Fact]
    public async Task GetHandleAsync_NotConnected_ReturnsNull()
    {
        var (actor, _, _) = CreateActor();
        var handle = await actor.GetHandleAsync();
        handle.ShouldBeNull();
    }

    // --- DisconnectAsync ---

    [Fact]
    public async Task DisconnectAsync_NeverActivated_DoesNotThrow()
    {
        var (actor, _, _) = CreateActor();
        await actor.DisconnectAsync(); // Should not throw
    }

    [Fact]
    public async Task DisconnectAsync_WhenConnected_ClearsHandle()
    {
        var (actor, connector, tokenSvc) = CreateActor();
        await actor.OnActivatedAsync("ws/tool", TestContext.Current.CancellationToken);

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "tool", Type = ToolType.Cli, IsConnected = true });

        var token = CreateToken(tokenSvc, workspaceId: "ws");
        var spec = new ToolSpec { Name = "tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Manifest.CliConfig() };
        await actor.ConnectAsync(spec, token);

        (await actor.GetHandleAsync()).ShouldNotBeNull();
        await actor.DisconnectAsync();
        (await actor.GetHandleAsync()).ShouldBeNull();
    }

    // --- EnsureIdentity fallback ---

    [Fact]
    public async Task ConnectAsync_NoActivation_UsesTokenWorkspaceId()
    {
        var (actor, connector, tokenSvc) = CreateActor();

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "new-tool", Type = ToolType.Cli, IsConnected = true });

        var token = CreateToken(tokenSvc);
        var spec = new ToolSpec { Name = "new-tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Manifest.CliConfig() };
        var handle = await actor.ConnectAsync(spec, token);
        handle.ToolName.ShouldBe("new-tool");
    }

    [Fact]
    public async Task InvokeAsync_ActivatedButNotConnected_Throws()
    {
        var (actor, _, tokenSvc) = CreateActor();
        await actor.OnActivatedAsync("ws/tool", TestContext.Current.CancellationToken);

        var token = CreateToken(tokenSvc, workspaceId: "ws");
        var invocation = new ToolInvocation { ToolName = "tool", Method = "run", Parameters = [] };

        await Should.ThrowAsync<InvalidOperationException>(() => actor.InvokeAsync(invocation, token));
    }

    [Fact]
    public async Task ConnectAsync_WithCrossWorkspaceToken_Throws()
    {
        var (actor, _, tokenSvc) = CreateActor();
        await actor.OnActivatedAsync("ws-a/tool", TestContext.Current.CancellationToken);

        var foreignToken = CreateToken(tokenSvc, workspaceId: "ws-b");
        var spec = new ToolSpec { Name = "tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Manifest.CliConfig() };

        await Should.ThrowAsync<UnauthorizedAccessException>(() => actor.ConnectAsync(spec, foreignToken));
    }

    [Fact]
    public async Task ConnectAsync_OnDeniedTokenWorkspace_PublishesEventWithReasonWorkspaceMismatch()
    {
        var bus = new ToolCapabilityCapturingEventBus();
        var (actor, _, tokenSvc) = CreateActor(bus);
        await actor.OnActivatedAsync("ws-a/git", TestContext.Current.CancellationToken);
        var foreignToken = CreateToken(tokenSvc, workspaceId: "ws-other");
        var spec = new ToolSpec { Name = "git", Type = ToolType.Cli, Cli = new Weave.Workspaces.Manifest.CliConfig() };

        await Should.ThrowAsync<UnauthorizedAccessException>(() => actor.ConnectAsync(spec, foreignToken));

        bus.CapabilityEvents.Count.ShouldBe(1);
        var evt = bus.CapabilityEvents[0];
        evt.Outcome.ShouldBe(CapabilityAuthorizationOutcome.Deny);
        evt.Reason.ShouldBe("workspace-mismatch");
        evt.Grant.ShouldBe("tool:git");
        evt.ActionContext.ShouldBe("ConnectAsync");
    }

    private sealed class ToolCapabilityCapturingEventBus : IEventBus
    {
        public List<CapabilityAuthorizationEvent> CapabilityEvents { get; } = [];

        public Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct) where TEvent : IDomainEvent
        {
            if (domainEvent is CapabilityAuthorizationEvent capabilityEvent)
                CapabilityEvents.Add(capabilityEvent);
            return Task.CompletedTask;
        }

        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IDomainEvent =>
            throw new NotSupportedException();
    }
}
