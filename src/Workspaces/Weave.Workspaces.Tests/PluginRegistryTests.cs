using Microsoft.Extensions.Logging.Abstractions;
using Weave.Workspaces.Models;
using Weave.Workspaces.Plugins;

namespace Weave.Workspaces.Tests;

public sealed class PluginRegistryTests
{
    private static PluginRegistry CreateRegistry(params IPluginConnector[] connectors) =>
        new(connectors, NullLogger<PluginRegistry>.Instance);

    [Fact]
    public async Task ConnectAsync_KnownType_ReturnsConnected()
    {
        var connector = new FakePluginConnector("dapr", connected: true);
        var registry = CreateRegistry(connector);

        var status = await registry.ConnectAsync("my-dapr", new PluginDefinition
        {
            Type = "dapr",
            Config = new Dictionary<string, string> { ["port"] = "3500" }
        });

        status.IsConnected.ShouldBeTrue();
        status.Name.ShouldBe("my-dapr");
        status.Type.ShouldBe("dapr");
    }

    [Fact]
    public async Task ConnectAsync_UnknownType_ReturnsError()
    {
        var registry = CreateRegistry();

        var status = await registry.ConnectAsync("mystery", new PluginDefinition { Type = "alien" });

        status.IsConnected.ShouldBeFalse();
        status.Error!.ShouldContain("alien");
    }

    [Fact]
    public async Task ConnectAllAsync_RegistersMultiple()
    {
        var dapr = new FakePluginConnector("dapr", connected: true);
        var vault = new FakePluginConnector("vault", connected: true);
        var registry = CreateRegistry(dapr, vault);

        var plugins = new Dictionary<string, PluginDefinition>
        {
            ["sidecar"] = new PluginDefinition { Type = "dapr" },
            ["secrets"] = new PluginDefinition { Type = "vault" }
        };

        var results = await registry.ConnectAllAsync(plugins);

        results.Count.ShouldBe(2);
        results.ShouldAllBe(s => s.IsConnected);
    }

    [Fact]
    public async Task DisconnectAsync_ActivePlugin_RemovesIt()
    {
        var connector = new FakePluginConnector("dapr", connected: true);
        var registry = CreateRegistry(connector);

        await registry.ConnectAsync("dapr-1", new PluginDefinition { Type = "dapr" });
        registry.GetAll().Count.ShouldBe(1);

        var status = await registry.DisconnectAsync("dapr-1");
        status.IsConnected.ShouldBeFalse();
        registry.GetAll().ShouldBeEmpty();
    }

    [Fact]
    public async Task DisconnectAsync_UnknownPlugin_ReturnsError()
    {
        var registry = CreateRegistry();

        var status = await registry.DisconnectAsync("nonexistent");

        status.IsConnected.ShouldBeFalse();
        status.Error!.ShouldContain("not active");
    }

    [Fact]
    public async Task GetAll_ReturnsAllActive()
    {
        var connector = new FakePluginConnector("http", connected: true);
        var registry = CreateRegistry(connector);

        await registry.ConnectAsync("api-1", new PluginDefinition { Type = "http" });
        await registry.ConnectAsync("api-2", new PluginDefinition { Type = "http" });

        registry.GetAll().Count.ShouldBe(2);
    }

    [Fact]
    public async Task ConnectAsync_AlreadyActive_HotSwapsPlugin()
    {
        var connector = new FakePluginConnector("dapr", connected: true);
        var registry = CreateRegistry(connector);

        var first = await registry.ConnectAsync("my-dapr", new PluginDefinition
        {
            Type = "dapr",
            Config = new Dictionary<string, string> { ["port"] = "3500" }
        });
        first.IsConnected.ShouldBeTrue();

        // Reconnect same name — should disconnect old, connect new
        var second = await registry.ConnectAsync("my-dapr", new PluginDefinition
        {
            Type = "dapr",
            Config = new Dictionary<string, string> { ["port"] = "3501" }
        });
        second.IsConnected.ShouldBeTrue();

        // Should still be one active plugin
        registry.GetAll().Count.ShouldBe(1);
        connector.DisconnectCount.ShouldBe(1);
    }

    [Fact]
    public async Task ConnectAsync_AlreadyActive_DifferentType_HotSwaps()
    {
        var dapr = new FakePluginConnector("dapr", connected: true);
        var webhook = new FakePluginConnector("webhook", connected: true);
        var registry = CreateRegistry(dapr, webhook);

        await registry.ConnectAsync("events", new PluginDefinition { Type = "dapr" });
        await registry.ConnectAsync("events", new PluginDefinition { Type = "webhook" });

        registry.GetAll().Count.ShouldBe(1);
        registry.GetAll()[0].Type.ShouldBe("webhook");
        dapr.DisconnectCount.ShouldBe(1);
    }

    [Fact]
    public async Task ConnectAsync_ConnectorThrows_ReturnsError()
    {
        var connector = new ThrowingPluginConnector("dapr");
        var registry = CreateRegistry(connector);

        var status = await registry.ConnectAsync("bad", new PluginDefinition { Type = "dapr" });

        status.IsConnected.ShouldBeFalse();
        status.Error!.ShouldContain("boom");
    }

    private sealed class FakePluginConnector(string type, bool connected) : IPluginConnector
    {
        public string PluginType => type;
        public int DisconnectCount { get; private set; }

        public Task<PluginStatus> ConnectAsync(string name, PluginDefinition definition) =>
            Task.FromResult<PluginStatus>(new() { Name = name, Type = type, IsConnected = connected });

        public Task<PluginStatus> DisconnectAsync(string name)
        {
            DisconnectCount++;
            return Task.FromResult<PluginStatus>(new() { Name = name, Type = type, IsConnected = false });
        }

        public PluginStatus GetStatus(string name) =>
            new() { Name = name, Type = type, IsConnected = connected };
    }

    private sealed class ThrowingPluginConnector(string type) : IPluginConnector
    {
        public string PluginType => type;

        public Task<PluginStatus> ConnectAsync(string name, PluginDefinition definition) =>
            throw new InvalidOperationException("boom");

        public Task<PluginStatus> DisconnectAsync(string name) =>
            Task.FromResult<PluginStatus>(new() { Name = name, Type = type, IsConnected = false });

        public PluginStatus GetStatus(string name) =>
            new() { Name = name, Type = type, IsConnected = false };
    }
}
