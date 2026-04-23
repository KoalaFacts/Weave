using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Weave.Shared.Plugins;
using Weave.Silo.Plugins;
using Weave.Workspaces.Models;
using Weave.Workspaces.Plugins;

namespace Weave.Silo.Tests.Plugins;

/// <summary>
/// Unit tests for <see cref="HttpPluginConnector"/> — validates connect/disconnect/status
/// lifecycle, base-url validation, and broker-keyed client storage.
/// </summary>
public sealed class HttpPluginConnectorTests
{
    private static (HttpPluginConnector Connector, PluginServiceBroker Broker) CreateConnector()
    {
        var broker = new PluginServiceBroker(NullLogger<PluginServiceBroker>.Instance);
        var clientFactory = Substitute.For<IHttpClientFactory>();
        clientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var connector = new HttpPluginConnector(broker, clientFactory, NullLoggerFactory.Instance);
        return (connector, broker);
    }

    private static PluginDefinition Def(string? baseUrl = null) => new()
    {
        Type = "http",
        Config = baseUrl is null ? [] : new Dictionary<string, string> { ["base_url"] = baseUrl }
    };

    [Fact]
    public void Schema_Advertises_BaseUrlAsRequired()
    {
        var (connector, _) = CreateConnector();

        connector.PluginType.ShouldBe("http");
        connector.Schema.Type.ShouldBe("http");
        connector.Schema.Provides.ShouldContain("http");
        connector.Schema.Config.ShouldContain(f => f.Name == "base_url" && f.Required);
    }

    [Fact]
    public async Task ConnectAsync_MissingBaseUrl_ReturnsErrorStatus()
    {
        var (connector, broker) = CreateConnector();

        var status = await connector.ConnectAsync("svc", Def(baseUrl: null));

        status.IsConnected.ShouldBeFalse();
        status.Error.ShouldNotBeNull();
        status.Error.ShouldContain("base_url");
        broker.Get<HttpClient>("http:svc").ShouldBeNull();
    }

    [Fact]
    public async Task ConnectAsync_ValidBaseUrl_StoresClientInBroker()
    {
        var (connector, broker) = CreateConnector();

        var status = await connector.ConnectAsync("svc", Def(baseUrl: "https://api.example.com"));

        status.IsConnected.ShouldBeTrue();
        status.Info["base_url"].ShouldBe("https://api.example.com");
        var client = broker.Get<HttpClient>("http:svc");
        client.ShouldNotBeNull();
        client!.BaseAddress.ShouldBe(new Uri("https://api.example.com"));
    }

    [Fact]
    public async Task DisconnectAsync_RemovesBrokerKeyWithoutDisposing()
    {
        var (connector, broker) = CreateConnector();
        await connector.ConnectAsync("svc", Def(baseUrl: "https://api.example.com"));

        var status = await connector.DisconnectAsync("svc");

        status.IsConnected.ShouldBeFalse();
        broker.Get<HttpClient>("http:svc").ShouldBeNull();
    }

    [Fact]
    public void GetStatus_Connected_ReturnsTrue()
    {
        var (connector, broker) = CreateConnector();
        broker.Set("http:svc", new HttpClient());

        var status = connector.GetStatus("svc");

        status.IsConnected.ShouldBeTrue();
    }

    [Fact]
    public void GetStatus_NotConnected_ReturnsFalse()
    {
        var (connector, _) = CreateConnector();

        var status = connector.GetStatus("svc");

        status.IsConnected.ShouldBeFalse();
    }
}
