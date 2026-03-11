using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Shared.Lifecycle;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Tools.Grains;
using Weave.Tools.Models;
using Xunit;

namespace Weave.Tools.Tests;

public sealed class ToolGrainTests
{
    private static (ToolGrain Grain, IToolConnector Connector, ICapabilityTokenService TokenService) CreateGrain()
    {
        var connector = Substitute.For<IToolConnector>();
        connector.ToolType.Returns(ToolType.Cli);

        var discovery = Substitute.For<IToolDiscoveryService>();
        discovery.GetConnector(ToolType.Cli).Returns(connector);

        var leakScanner = new LeakScanner(Substitute.For<ILogger<LeakScanner>>());
        var tokenService = new CapabilityTokenService();
        var lifecycleManager = Substitute.For<ILifecycleManager>();
        var logger = Substitute.For<ILogger<ToolGrain>>();

        var grain = new ToolGrain(discovery, leakScanner, tokenService, lifecycleManager, logger);
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

        handle.Should().NotBeNull();
        handle.ToolName.Should().Be("test-tool");
        handle.IsConnected.Should().BeTrue();
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
        var act = () => grain.ConnectAsync(definition, token);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
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

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("secret leak");
    }
}
