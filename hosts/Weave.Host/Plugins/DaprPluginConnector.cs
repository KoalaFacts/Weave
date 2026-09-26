using Weave.Shared.Events;
using Weave.Shared.Plugins;
using Weave.Silo.Events;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;
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
    private readonly PluginActivationStore _activations = new();

    public string PluginType => "dapr";

    public IReadOnlyList<string> RegistrationKeys(string name) => ["event-bus", "tool:dapr"];

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

        // Prepare both resources before changing runtime registrations.
        var eventClient = httpClientFactory.CreateClient($"dapr-plugin:{name}");
        eventClient.BaseAddress = new Uri(baseUrl);
        var eventBus = new DaprEventBus(eventClient, loggerFactory.CreateLogger<DaprEventBus>());

        var toolClient = httpClientFactory.CreateClient($"dapr-tool:{name}");
        toolClient.BaseAddress = new Uri(baseUrl);
        var toolConnector = new DaprToolConnector(toolClient, loggerFactory.CreateLogger<DaprToolConnector>());

        // Apply both registrations as one owned activation; roll back if either fails.
        // Don't dispose the previous bus. HttpClient lifetime is managed by
        // IHttpClientFactory. In-flight PublishAsync calls on the old bus
        // will complete safely against the still-valid HttpClient handler.
        var scope = new PluginActivationScope();
        IEventBus? displacedBus = null;
        scope.Add(() => displacedBus = broker.Swap<IEventBus>(eventBus), () =>
        {
            if (displacedBus is null)
                broker.ClearIfCurrent<IEventBus>(eventBus);
            else
                broker.ReplaceIfCurrent<IEventBus>(eventBus, displacedBus);
        });
        scope.Add(() => toolDiscovery.Register(toolConnector), () =>
        {
            toolConnector.Deactivate();
            toolDiscovery.UnregisterIfCurrent(ToolType.Dapr, toolConnector);
        });
        try
        {
            _activations.Replace(name, scope);
        }
        finally
        {
            // A later disconnect must clear this installation, not restore a replaced one.
            displacedBus = null;
        }

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
        _activations.Remove(name);

        LogDaprDisconnected(name);
        return Task.FromResult(new PluginStatus { Name = name, Type = PluginType, IsConnected = false });
    }

    public PluginStatus GetStatus(string name)
    {
        return new PluginStatus { Name = name, Type = PluginType, IsConnected = _activations.Contains(name) };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dapr plugin '{Name}' connected — sidecar at {BaseUrl}")]
    private partial void LogDaprConnected(string name, string baseUrl);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dapr plugin '{Name}' disconnected")]
    private partial void LogDaprDisconnected(string name);
}
