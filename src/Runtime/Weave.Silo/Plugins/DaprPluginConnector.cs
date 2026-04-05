using Microsoft.Extensions.Logging;
using Weave.Shared.Events;
using Weave.Shared.Plugins;
using Weave.Silo.Events;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Workspaces.Models;
using Weave.Workspaces.Plugins;

namespace Weave.Silo.Plugins;

/// <summary>
/// Connects the Dapr plugin — swaps in <see cref="DaprEventBus"/> and registers
/// <see cref="DaprToolConnector"/> via the broker and discovery service.
/// Hot-swappable: disconnect reverts to defaults.
/// </summary>
public sealed partial class DaprPluginConnector(
    PluginServiceBroker broker,
    IToolDiscoveryService toolDiscovery,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory) : IPluginConnector
{
    private readonly ILogger<DaprPluginConnector> _logger = loggerFactory.CreateLogger<DaprPluginConnector>();

    public string PluginType => "dapr";

    public PluginSchema Schema { get; } = new()
    {
        Type = "dapr",
        Description = "Dapr sidecar — provides event bus and service invocation via the Dapr HTTP API",
        Provides = ["events", "tools"],
        Config =
        [
            new() { Name = "port", Description = "Dapr sidecar HTTP port", EnvVar = "DAPR_HTTP_PORT" },
        ]
    };

    public Task<PluginStatus> ConnectAsync(string name, PluginDefinition definition)
    {
        // PluginRegistry.ResolveConfig already fills config from DAPR_HTTP_PORT env var
        var port = definition.Config.GetValueOrDefault("port");

        if (port is null)
        {
            return Task.FromResult(new PluginStatus
            {
                Name = name,
                Type = PluginType,
                IsConnected = false,
                Error = "No Dapr sidecar port found. Set 'port' in plugin config or DAPR_HTTP_PORT env var."
            });
        }

        var baseUrl = $"http://localhost:{port}";

        // B6 fix: Create ALL resources before swapping anything, so a failure
        // in tool setup doesn't leave a half-swapped event bus.
        var eventClient = httpClientFactory.CreateClient($"dapr-plugin:{name}");
        eventClient.BaseAddress = new Uri(baseUrl);
        var eventBus = new DaprEventBus(eventClient, loggerFactory.CreateLogger<DaprEventBus>());

        var toolClient = httpClientFactory.CreateClient($"dapr-tool:{name}");
        toolClient.BaseAddress = new Uri(baseUrl);
        var toolConnector = new DaprToolConnector(toolClient, loggerFactory.CreateLogger<DaprToolConnector>());

        // Now swap atomically — both resources are ready
        // B1 fix: Don't dispose previous. HttpClient lifetime is managed by
        // IHttpClientFactory. In-flight PublishAsync calls on the old bus
        // will complete safely against the still-valid HttpClient handler.
        broker.Swap<IEventBus>(eventBus);
        toolDiscovery.Register(toolConnector);

        LogDaprConnected(name, baseUrl);

        return Task.FromResult(new PluginStatus
        {
            Name = name,
            Type = PluginType,
            IsConnected = true,
            Info = new Dictionary<string, string> { ["sidecar"] = baseUrl }
        });
    }

    public Task<PluginStatus> DisconnectAsync(string name)
    {
        // M3 fix: Only clear the slot if we still own it
        if (broker.Get<IEventBus>() is DaprEventBus)
            broker.Swap<IEventBus>(null);

        toolDiscovery.Unregister(Tools.Models.ToolType.Dapr);

        LogDaprDisconnected(name);
        return Task.FromResult(new PluginStatus { Name = name, Type = PluginType, IsConnected = false });
    }

    public PluginStatus GetStatus(string name)
    {
        var hasBus = broker.Get<IEventBus>() is DaprEventBus;
        return new PluginStatus { Name = name, Type = PluginType, IsConnected = hasBus };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dapr plugin '{Name}' connected — sidecar at {BaseUrl}")]
    private partial void LogDaprConnected(string name, string baseUrl);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dapr plugin '{Name}' disconnected")]
    private partial void LogDaprDisconnected(string name);
}
