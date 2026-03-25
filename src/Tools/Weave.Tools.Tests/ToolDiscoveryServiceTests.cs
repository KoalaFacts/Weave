using Microsoft.Extensions.Logging;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Tools.Models;

namespace Weave.Tools.Tests;

public sealed class ToolDiscoveryServiceTests
{
    private static IToolConnector CreateConnector(ToolType type)
    {
        var connector = Substitute.For<IToolConnector>();
        connector.ToolType.Returns(type);
        return connector;
    }

    private static ToolDiscoveryService CreateService(params IToolConnector[] connectors) =>
        new(connectors, Substitute.For<ILogger<ToolDiscoveryService>>());

    [Fact]
    public void GetConnector_WithRegisteredType_ReturnsCorrectConnector()
    {
        var cliConnector = CreateConnector(ToolType.Cli);
        var mcpConnector = CreateConnector(ToolType.Mcp);
        var service = CreateService(cliConnector, mcpConnector);

        service.GetConnector(ToolType.Cli).ShouldBeSameAs(cliConnector);
        service.GetConnector(ToolType.Mcp).ShouldBeSameAs(mcpConnector);
    }

    [Fact]
    public void GetConnector_WithUnregisteredType_ThrowsNotSupported()
    {
        var service = CreateService(CreateConnector(ToolType.Cli));

        var ex = Should.Throw<NotSupportedException>(() => service.GetConnector(ToolType.Dapr));
        ex.Message.ShouldContain("Dapr");
    }

    [Fact]
    public void SupportedTypes_ReturnsAllRegistered()
    {
        var service = CreateService(
            CreateConnector(ToolType.Cli),
            CreateConnector(ToolType.Mcp),
            CreateConnector(ToolType.OpenApi));

        service.SupportedTypes.Count.ShouldBe(3);
        service.SupportedTypes.ShouldContain(ToolType.Cli);
        service.SupportedTypes.ShouldContain(ToolType.Mcp);
        service.SupportedTypes.ShouldContain(ToolType.OpenApi);
    }

    [Fact]
    public void SupportedTypes_WithNoConnectors_ReturnsEmpty()
    {
        var service = CreateService();

        service.SupportedTypes.ShouldBeEmpty();
    }

    [Fact]
    public void GetConnector_WithNoConnectors_ThrowsNotSupported()
    {
        var service = CreateService();

        Should.Throw<NotSupportedException>(() => service.GetConnector(ToolType.Cli));
    }

    [Fact]
    public void Register_DynamicConnector_ResolvesAtRuntime()
    {
        var service = CreateService(CreateConnector(ToolType.Cli));
        var daprConnector = CreateConnector(ToolType.Dapr);

        service.Register(daprConnector);

        service.GetConnector(ToolType.Dapr).ShouldBeSameAs(daprConnector);
        service.SupportedTypes.ShouldContain(ToolType.Dapr);
    }

    [Fact]
    public void Register_DynamicConnector_OverridesBuiltIn()
    {
        var builtIn = CreateConnector(ToolType.Cli);
        var replacement = CreateConnector(ToolType.Cli);
        var service = CreateService(builtIn);

        service.Register(replacement);

        service.GetConnector(ToolType.Cli).ShouldBeSameAs(replacement);
    }

    [Fact]
    public void Unregister_DynamicConnector_RevertsToBuiltIn()
    {
        var builtIn = CreateConnector(ToolType.Cli);
        var replacement = CreateConnector(ToolType.Cli);
        var service = CreateService(builtIn);

        service.Register(replacement);
        service.GetConnector(ToolType.Cli).ShouldBeSameAs(replacement);

        service.Unregister(ToolType.Cli);
        service.GetConnector(ToolType.Cli).ShouldBeSameAs(builtIn);
    }

    [Fact]
    public void Unregister_DynamicOnlyConnector_ThrowsAfterRemoval()
    {
        var service = CreateService();
        var daprConnector = CreateConnector(ToolType.Dapr);

        service.Register(daprConnector);
        service.Unregister(ToolType.Dapr);

        Should.Throw<NotSupportedException>(() => service.GetConnector(ToolType.Dapr));
    }

    [Fact]
    public void Unregister_NonexistentType_ReturnsFalse()
    {
        var service = CreateService();

        service.Unregister(ToolType.Dapr).ShouldBeFalse();
    }
}
