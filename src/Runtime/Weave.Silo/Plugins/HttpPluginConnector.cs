using Microsoft.Extensions.Logging;
using Weave.Shared.Plugins;
using Weave.Workspaces.Models;
using Weave.Workspaces.Plugins;

namespace Weave.Silo.Plugins;

/// <summary>
/// Connects a generic HTTP plugin — stores a named <see cref="HttpClient"/>
/// in the broker for plugin-specific HTTP integrations.
/// </summary>
public sealed partial class HttpPluginConnector(
    PluginServiceBroker broker,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory) : IPluginConnector
{
    private readonly ILogger<HttpPluginConnector> _logger = loggerFactory.CreateLogger<HttpPluginConnector>();

    public string PluginType => "http";

    public Task<PluginStatus> ConnectAsync(string name, PluginDefinition definition)
    {
        var baseUrl = definition.Config.GetValueOrDefault("base_url");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return Task.FromResult(new PluginStatus
            {
                Name = name,
                Type = PluginType,
                IsConnected = false,
                Error = "HTTP plugin requires 'base_url' in config."
            });
        }

        var httpClient = httpClientFactory.CreateClient($"plugin:{name}");
        httpClient.BaseAddress = new Uri(baseUrl);
        broker.Set($"http:{name}", httpClient);

        LogHttpConnected(name, baseUrl);

        return Task.FromResult(new PluginStatus
        {
            Name = name,
            Type = PluginType,
            IsConnected = true,
            Info = new Dictionary<string, string> { ["base_url"] = baseUrl }
        });
    }

    public Task<PluginStatus> DisconnectAsync(string name)
    {
        var previous = broker.Remove($"http:{name}");
        if (previous is IDisposable disposable)
            disposable.Dispose();

        return Task.FromResult(new PluginStatus { Name = name, Type = PluginType, IsConnected = false });
    }

    public PluginStatus GetStatus(string name)
    {
        var client = broker.Get<HttpClient>($"http:{name}");
        return new PluginStatus { Name = name, Type = PluginType, IsConnected = client is not null };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "HTTP plugin '{Name}' connected — base URL {BaseUrl}")]
    private partial void LogHttpConnected(string name, string baseUrl);
}
