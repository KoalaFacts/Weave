using Microsoft.Extensions.Logging;
using Weave.Security.Grains;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Tools.Grains;
using Weave.Tools.Models;

namespace Weave.Tools.Tests;

public sealed class ToolGrainTests
{
    private static (ToolGrain Grain, IToolConnector Connector, ICapabilityTokenService TokenService) CreateGrain()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var connector = Substitute.For<IToolConnector>();
        connector.ToolType.Returns(ToolType.Cli);

        var discovery = Substitute.For<IToolDiscoveryService>();
        discovery.GetConnector(ToolType.Cli).Returns(connector);

        var leakScanner = new LeakScanner(Substitute.For<ILogger<LeakScanner>>());
        var tokenService = new CapabilityTokenService();
        var lifecycleManager = Substitute.For<ILifecycleManager>();
        var logger = Substitute.For<ILogger<ToolGrain>>();
        var eventBus = Substitute.For<IEventBus>();
        var secretProxy = Substitute.For<ISecretProxyGrain>();
        secretProxy.SubstituteAsync(Arg.Any<string>()).Returns(callInfo => callInfo.Arg<string>());

        grainFactory.GetGrain<ISecretProxyGrain>(Arg.Any<string>(), null).Returns(secretProxy);

        var grain = new ToolGrain(grainFactory, discovery, leakScanner, tokenService, lifecycleManager, eventBus, logger);
        return (grain, connector, tokenService);
    }

    private static CapabilityToken CreateToken(ICapabilityTokenService svc) =>
        svc.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "test",
            IssuedTo = "agent",
            Grants = ["tool:*", "secret:*"],
            Lifetime = TimeSpan.FromHours(1)
        });

    [Fact]
    public async Task ConnectAsync_WithValidToken_ReturnsHandle()
    {
        var (grain, connector, tokenSvc) = CreateGrain();
        var token = CreateToken(tokenSvc);

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "test-tool", Type = ToolType.Cli, IsConnected = true });

        var definition = new ToolSpec { Name = "test-tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Models.CliConfig() };
        var handle = await grain.ConnectAsync(definition, token);

        handle.ShouldNotBeNull();
        handle.ToolName.ShouldBe("test-tool");
        handle.IsConnected.ShouldBeTrue();
    }

    [Fact]
    public async Task ConnectAsync_WithInvalidToken_Throws()
    {
        var (grain, _, tokenSvc) = CreateGrain();
        var token = tokenSvc.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "test",
            IssuedTo = "agent",
            Grants = ["*"],
            Lifetime = TimeSpan.FromMilliseconds(-1)
        });

        var definition = new ToolSpec { Name = "tool", Type = ToolType.Cli };
        await Should.ThrowAsync<UnauthorizedAccessException>(() => grain.ConnectAsync(definition, token));
    }

    [Fact]
    public async Task InvokeAsync_WithSecretInPayload_BlocksInvocation()
    {
        var (grain, connector, tokenSvc) = CreateGrain();
        var token = CreateToken(tokenSvc);

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "tool", Type = ToolType.Cli, IsConnected = true });

        var definition = new ToolSpec { Name = "tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Models.CliConfig() };
        await grain.ConnectAsync(definition, token);

        var invocation = new ToolInvocation
        {
            ToolName = "tool",
            Method = "exec",
            RawInput = "AKIAIOSFODNN7EXAMPLE"
        };

        var result = await grain.InvokeAsync(invocation, token);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("secret leak");
    }

    // --- DisconnectAsync ---

    [Fact]
    public async Task DisconnectAsync_WhenConnected_CallsConnectorDisconnect()
    {
        var (grain, connector, tokenSvc) = CreateGrain();
        var token = CreateToken(tokenSvc);

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "tool", Type = ToolType.Cli, IsConnected = true });

        var definition = new ToolSpec { Name = "tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Models.CliConfig() };
        await grain.ConnectAsync(definition, token);

        await grain.DisconnectAsync();

        await connector.Received(1).DisconnectAsync(Arg.Any<ToolHandle>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisconnectAsync_WhenNotConnected_DoesNotThrow()
    {
        var (grain, connector, _) = CreateGrain();

        // Disconnect without ever connecting — should be a no-op
        await grain.DisconnectAsync();

        await connector.DidNotReceive().DisconnectAsync(Arg.Any<ToolHandle>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisconnectAsync_AfterDisconnect_HandleIsNull()
    {
        var (grain, connector, tokenSvc) = CreateGrain();
        var token = CreateToken(tokenSvc);

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "tool", Type = ToolType.Cli, IsConnected = true });

        await grain.ConnectAsync(new ToolSpec { Name = "tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Models.CliConfig() }, token);
        await grain.DisconnectAsync();

        var handle = await grain.GetHandleAsync();
        handle.ShouldBeNull();
    }

    // --- GetHandleAsync ---

    [Fact]
    public async Task GetHandleAsync_WhenNotConnected_ReturnsNull()
    {
        var (grain, _, _) = CreateGrain();

        var handle = await grain.GetHandleAsync();

        handle.ShouldBeNull();
    }

    [Fact]
    public async Task GetHandleAsync_WhenConnected_ReturnsHandle()
    {
        var (grain, connector, tokenSvc) = CreateGrain();
        var token = CreateToken(tokenSvc);

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "tool", Type = ToolType.Cli, ConnectionId = "cli:tool:abc", IsConnected = true });

        await grain.ConnectAsync(new ToolSpec { Name = "tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Models.CliConfig() }, token);

        var handle = await grain.GetHandleAsync();
        handle.ShouldNotBeNull();
        handle!.IsConnected.ShouldBeTrue();
    }

    // --- GetSchemaAsync ---

    [Fact]
    public async Task GetSchemaAsync_WhenNotConnected_ReturnsNotConnectedDescription()
    {
        var (grain, _, _) = CreateGrain();

        var schema = await grain.GetSchemaAsync();

        schema.Description.ShouldContain("not connected");
    }

    [Fact]
    public async Task GetSchemaAsync_WhenConnected_DelegatesToConnector()
    {
        var (grain, connector, tokenSvc) = CreateGrain();
        var token = CreateToken(tokenSvc);

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "tool", Type = ToolType.Cli, IsConnected = true });
        connector.DiscoverSchemaAsync(Arg.Any<ToolHandle>(), Arg.Any<CancellationToken>())
            .Returns(new ToolSchema { ToolName = "tool", Description = "A test CLI tool" });

        await grain.ConnectAsync(new ToolSpec { Name = "tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Models.CliConfig() }, token);

        var schema = await grain.GetSchemaAsync();
        schema.Description.ShouldBe("A test CLI tool");
    }

    // --- InvokeAsync: not connected ---

    [Fact]
    public async Task InvokeAsync_NotConnected_Throws()
    {
        var (grain, _, tokenSvc) = CreateGrain();
        var token = CreateToken(tokenSvc);
        var invocation = new ToolInvocation { ToolName = "tool", Method = "run", Parameters = [] };

        await Should.ThrowAsync<InvalidOperationException>(() => grain.InvokeAsync(invocation, token));
    }

    // --- InvokeAsync: invalid token ---

    [Fact]
    public async Task InvokeAsync_InvalidToken_Throws()
    {
        var (grain, connector, tokenSvc) = CreateGrain();
        var token = CreateToken(tokenSvc);

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "tool", Type = ToolType.Cli, IsConnected = true });

        await grain.ConnectAsync(new ToolSpec { Name = "tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Models.CliConfig() }, token);

        var expired = token with { Signature = "tampered" };
        var invocation = new ToolInvocation { ToolName = "tool", Method = "run", Parameters = [] };

        await Should.ThrowAsync<UnauthorizedAccessException>(() => grain.InvokeAsync(invocation, expired));
    }

    // --- InvokeAsync: success path ---

    [Fact]
    public async Task InvokeAsync_CleanPayload_ReturnsResult()
    {
        var (grain, connector, tokenSvc) = CreateGrain();
        var token = CreateToken(tokenSvc);

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "tool", Type = ToolType.Cli, IsConnected = true });
        connector.InvokeAsync(Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new ToolResult { Success = true, ToolName = "tool", Output = "hello world" });

        await grain.ConnectAsync(new ToolSpec { Name = "tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Models.CliConfig() }, token);

        var invocation = new ToolInvocation { ToolName = "tool", Method = "echo", RawInput = "safe input", Parameters = [] };
        var result = await grain.InvokeAsync(invocation, token);

        result.Success.ShouldBeTrue();
        result.Output.ShouldBe("hello world");
    }

    // --- InvokeAsync: response leak redaction ---

    [Fact]
    public async Task InvokeAsync_LeakInResponse_RedactsOutput()
    {
        var (grain, connector, tokenSvc) = CreateGrain();
        var token = CreateToken(tokenSvc);

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "tool", Type = ToolType.Cli, IsConnected = true });
        connector.InvokeAsync(Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new ToolResult { Success = true, ToolName = "tool", Output = "result: AKIAIOSFODNN7EXAMPLE" });

        await grain.ConnectAsync(new ToolSpec { Name = "tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Models.CliConfig() }, token);

        var invocation = new ToolInvocation { ToolName = "tool", Method = "fetch", RawInput = "safe", Parameters = [] };
        var result = await grain.InvokeAsync(invocation, token);

        result.Output.ShouldContain("REDACTED");
        result.Output.ShouldNotContain("AKIAIOSFODNN7EXAMPLE");
    }

    // --- InvokeAsync: failed result with clean output skips response scan ---

    [Fact]
    public async Task InvokeAsync_FailedResult_DoesNotScanResponse()
    {
        var (grain, connector, tokenSvc) = CreateGrain();
        var token = CreateToken(tokenSvc);

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "tool", Type = ToolType.Cli, IsConnected = true });
        connector.InvokeAsync(Arg.Any<ToolHandle>(), Arg.Any<ToolInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new ToolResult { Success = false, ToolName = "tool", Output = "AKIAIOSFODNN7EXAMPLE", Error = "process failed" });

        await grain.ConnectAsync(new ToolSpec { Name = "tool", Type = ToolType.Cli, Cli = new Weave.Workspaces.Models.CliConfig() }, token);

        var invocation = new ToolInvocation { ToolName = "tool", Method = "run", RawInput = "safe", Parameters = [] };
        var result = await grain.InvokeAsync(invocation, token);

        // Failed results skip response scanning — the leak in output is NOT redacted
        result.Success.ShouldBeFalse();
        result.Output.ShouldContain("AKIAIOSFODNN7EXAMPLE");
    }

    // --- ConnectAsync: token without tool grant ---

    [Fact]
    public async Task ConnectAsync_TokenWithoutToolGrant_Throws()
    {
        var (grain, _, tokenSvc) = CreateGrain();
        var token = tokenSvc.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "test",
            IssuedTo = "agent",
            Grants = ["secret:*"],  // no tool:* grant
            Lifetime = TimeSpan.FromHours(1)
        });

        var definition = new ToolSpec { Name = "tool", Type = ToolType.Cli };
        await Should.ThrowAsync<UnauthorizedAccessException>(() => grain.ConnectAsync(definition, token));
    }
}
