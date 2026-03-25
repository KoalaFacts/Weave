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

    public async Task<PluginStatus> ConnectAsync(string name, PluginDefinition definition)
    {
        var port = definition.Config.GetValueOrDefault("port")
            ?? Environment.GetEnvironmentVariable("DAPR_HTTP_PORT");

        if (port is null)
        {
            return new PluginStatus
            {
                Name = name,
                Type = PluginType,
                IsConnected = false,
                Error = "No Dapr sidecar port found. Set 'port' in plugin config or DAPR_HTTP_PORT env var."
            };
        }

        var baseUrl = $"http://localhost:{port}";

        // Swap event bus to Dapr-backed implementation
        var httpClient = httpClientFactory.CreateClient($"dapr-plugin:{name}");
        httpClient.BaseAddress = new Uri(baseUrl);
        var eventBus = new DaprEventBus(httpClient, loggerFactory.CreateLogger<DaprEventBus>());
        var previous = broker.Swap<IEventBus>(eventBus);
        await PluginDisposal.DisposeIfNeededAsync(previous);

        // Register Dapr tool connector dynamically
        var toolClient = httpClientFactory.CreateClient($"dapr-tool:{name}");
        toolClient.BaseAddress = new Uri(baseUrl);
        var toolConnector = new DaprToolConnector(toolClient, loggerFactory.CreateLogger<DaprToolConnector>());
        toolDiscovery.Register(toolConnector);

        LogDaprConnected(name, baseUrl);

        return new PluginStatus
        {
            Name = name,
            Type = PluginType,
            IsConnected = true,
            Info = new Dictionary<string, string> { ["sidecar"] = baseUrl }
        };
    }

    public async Task<PluginStatus> DisconnectAsync(string name)
    {
        var previous = broker.Swap<IEventBus>(null);
        await PluginDisposal.DisposeIfNeededAsync(previous);
        toolDiscovery.Unregister(Tools.Models.ToolType.Dapr);

        LogDaprDisconnected(name);
        return new PluginStatus { Name = name, Type = PluginType, IsConnected = false };
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
