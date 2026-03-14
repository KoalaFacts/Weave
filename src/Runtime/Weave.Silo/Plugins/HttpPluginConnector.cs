using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weave.Tools.Connectors;
using Weave.Workspaces.Models;
using Weave.Workspaces.Plugins;

namespace Weave.Silo.Plugins;

/// <summary>
/// Connects a generic HTTP plugin — registers an <see cref="OpenApiToolConnector"/>
/// pointed at the configured base URL. Useful for custom REST/OpenAPI endpoints.
/// </summary>
public sealed partial class HttpPluginConnector(
    IServiceCollection services,
    ILogger<HttpPluginConnector> logger) : IPluginConnector
{
    public string PluginType => "http";

    public PluginStatus Connect(string name, PluginDefinition definition)
    {
        var baseUrl = definition.Config.GetValueOrDefault("base_url");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new PluginStatus
            {
                Name = name,
                Type = PluginType,
                IsConnected = false,
                Error = "HTTP plugin requires 'base_url' in config."
            };
        }

        services.AddHttpClient($"plugin:{name}", c => c.BaseAddress = new Uri(baseUrl));

        LogHttpConnected(name, baseUrl);

        return new PluginStatus
        {
            Name = name,
            Type = PluginType,
            IsConnected = true,
            Info = new Dictionary<string, string> { ["base_url"] = baseUrl }
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

    [LoggerMessage(Level = LogLevel.Information, Message = "HTTP plugin '{Name}' connected — base URL {BaseUrl}")]
    private partial void LogHttpConnected(string name, string baseUrl);
}
