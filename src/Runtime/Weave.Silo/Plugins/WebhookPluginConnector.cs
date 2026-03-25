using Microsoft.Extensions.Logging;
using Weave.Shared.Events;
using Weave.Shared.Plugins;
using Weave.Silo.Events;
using Weave.Workspaces.Models;
using Weave.Workspaces.Plugins;

namespace Weave.Silo.Plugins;

/// <summary>
/// Connects the webhook plugin — swaps in <see cref="WebhookEventBus"/>
/// via the broker. Hot-swappable: disconnect reverts to the default event bus.
/// </summary>
public sealed partial class WebhookPluginConnector(
    PluginServiceBroker broker,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory) : IPluginConnector
{
    private readonly ILogger<WebhookPluginConnector> _logger = loggerFactory.CreateLogger<WebhookPluginConnector>();

    public string PluginType => "webhook";

    public PluginStatus Connect(string name, PluginDefinition definition)
    {
        var url = definition.Config.GetValueOrDefault("url");
        if (string.IsNullOrWhiteSpace(url))
        {
            return new PluginStatus
            {
                Name = name,
                Type = PluginType,
                IsConnected = false,
                Error = "Webhook plugin requires 'url' in config."
            };
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var webhookUri))
        {
            return new PluginStatus
            {
                Name = name,
                Type = PluginType,
                IsConnected = false,
                Error = $"Invalid webhook URL: '{url}'"
            };
        }

        var httpClient = httpClientFactory.CreateClient($"webhook-plugin:{name}");
        var eventBus = new WebhookEventBus(
            httpClient,
            webhookUri,
            loggerFactory.CreateLogger<WebhookEventBus>());

        var previous = broker.Swap<IEventBus>(eventBus);
        DisposeIfNeeded(previous);

        LogWebhookConnected(name, url);

        return new PluginStatus
        {
            Name = name,
            Type = PluginType,
            IsConnected = true,
            Info = new Dictionary<string, string> { ["url"] = url }
        };
    }

    public PluginStatus Disconnect(string name)
    {
        var previous = broker.Swap<IEventBus>(null);
        DisposeIfNeeded(previous);

        LogWebhookDisconnected(name);
        return new PluginStatus { Name = name, Type = PluginType, IsConnected = false };
    }

    public PluginStatus GetStatus(string name)
    {
        var hasBus = broker.Get<IEventBus>() is WebhookEventBus;
        return new PluginStatus { Name = name, Type = PluginType, IsConnected = hasBus };
    }

    private static void DisposeIfNeeded(object? instance)
    {
        if (instance is IAsyncDisposable asyncDisposable)
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        else if (instance is IDisposable disposable)
            disposable.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook plugin '{Name}' connected — posting to {Url}")]
    private partial void LogWebhookConnected(string name, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook plugin '{Name}' disconnected")]
    private partial void LogWebhookDisconnected(string name);
}
