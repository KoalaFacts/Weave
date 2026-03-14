using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weave.Shared.Events;
using Weave.Silo.Events;
using Weave.Tools.Connectors;
using Weave.Workspaces.Models;
using Weave.Workspaces.Plugins;

namespace Weave.Silo.Plugins;

/// <summary>
/// Connects the Dapr plugin — registers <see cref="DaprEventBus"/> and <see cref="DaprToolConnector"/>
/// backed by the Dapr sidecar HTTP API.
/// </summary>
public sealed partial class DaprPluginConnector(
    IServiceCollection services,
    ILogger<DaprPluginConnector> logger) : IPluginConnector
{
    public string PluginType => "dapr";

    public PluginStatus Connect(string name, PluginDefinition definition)
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
        services.AddHttpClient<DaprEventBus>(c => c.BaseAddress = new Uri(baseUrl));
        services.AddSingleton<IEventBus, DaprEventBus>();
        services.AddHttpClient<DaprToolConnector>(c => c.BaseAddress = new Uri(baseUrl));
        services.AddSingleton<IToolConnector>(sp => sp.GetRequiredService<DaprToolConnector>());

        LogDaprConnected(name, baseUrl);

        return new PluginStatus
        {
            Name = name,
            Type = PluginType,
            IsConnected = true,
            Info = new Dictionary<string, string> { ["sidecar"] = baseUrl }
        };
    }

    public PluginStatus Disconnect(string name)
    {
        return new PluginStatus { Name = name, Type = PluginType, IsConnected = false };
    }

    public PluginStatus GetStatus(string name)
    {
        return new PluginStatus { Name = name, Type = PluginType, IsConnected = true };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dapr plugin '{Name}' connected — sidecar at {BaseUrl}")]
    private partial void LogDaprConnected(string name, string baseUrl);
}
