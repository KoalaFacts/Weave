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
}
