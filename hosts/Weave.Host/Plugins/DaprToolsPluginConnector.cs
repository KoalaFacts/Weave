using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;

namespace Weave.Silo.Plugins;

/// <summary>Contributes Dapr service invocation without replacing the Host event bus.</summary>
public sealed class DaprToolsPluginConnector(
    IToolDiscoveryService toolDiscovery,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory) : IPluginConnector
{
    private readonly PluginActivationStore _activations = new();

    public string PluginType => "dapr_tools";

    public IReadOnlyList<string> RegistrationKeys(string name) => [$"tool:dapr:{name}"];

    public PluginSchema Schema { get; } = new()
    {
        Type = "dapr_tools",
        Description = "Dapr service invocation through an existing local sidecar",
        Provides = ["tools"],
        Config = [new PluginConfigField
        {
            Name = "port",
            Description = "Dapr sidecar HTTP port",
            EnvVar = "DAPR_HTTP_PORT",
            Required = true
        }]
    };

    public Task<PluginStatus> ConnectAsync(string name, PluginDefinition definition)
    {
        var portText = definition.Config.GetValueOrDefault("port");
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
            return Task.FromResult(new PluginStatus
            {
                Name = name,
                Type = PluginType,
                Error = "Dapr sidecar port must be between 1 and 65535."
            });

        var client = httpClientFactory.CreateClient($"dapr-tool:{name}");
        client.BaseAddress = new Uri($"http://localhost:{port}");
        var connector = new DaprToolConnector(client, loggerFactory.CreateLogger<DaprToolConnector>());
        var scope = new PluginActivationScope();
        scope.Add(() => toolDiscovery.Register(name, connector), () =>
        {
            connector.Deactivate();
            toolDiscovery.UnregisterIfCurrent(name, ToolType.Dapr, connector);
            client.Dispose();
        });
        _activations.Replace(name, scope);

        return Task.FromResult(new PluginStatus
        {
            Name = name,
            Type = PluginType,
            IsConnected = true
        });
    }

    public Task<PluginStatus> DisconnectAsync(string name)
    {
        _activations.Remove(name);
        return Task.FromResult(new PluginStatus { Name = name, Type = PluginType });
    }

    public PluginStatus GetStatus(string name) => new()
    {
        Name = name,
        Type = PluginType,
        IsConnected = _activations.Contains(name)
    };
}
