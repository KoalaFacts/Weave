using Weave.Shared.Plugins;
using Weave.Workspaces.Manifest;
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
    private readonly PluginActivationStore _activations = new();

    public string PluginType => "http";

    public IReadOnlyList<string> RegistrationKeys(string name) => [$"http:{name}"];

    public PluginSchema Schema { get; } = new()
    {
        Type = "http",
        Description = "Generic HTTP endpoint — provides a named HttpClient for custom integrations",
        Provides = ["http"],
        Config =
        [
            new() { Name = "base_url", Description = "Base URL of the HTTP service", Required = true },
        ]
    };

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
        var key = $"http:{name}";
        var scope = new PluginActivationScope();
        scope.Add(() => broker.Set(key, httpClient), () => broker.RemoveIfCurrent(key, httpClient));
        _activations.Replace(name, scope);

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
        // Remove from broker but don't dispose — HttpClient lifetime is managed
        // by IHttpClientFactory. Disposing would fault in-flight requests.
        _activations.Remove(name);

        return Task.FromResult(new PluginStatus { Name = name, Type = PluginType, IsConnected = false });
    }

    public PluginStatus GetStatus(string name)
    {
        return new PluginStatus { Name = name, Type = PluginType, IsConnected = _activations.Contains(name) };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "HTTP plugin '{Name}' connected — base URL {BaseUrl}")]
    private partial void LogHttpConnected(string name, string baseUrl);
}
