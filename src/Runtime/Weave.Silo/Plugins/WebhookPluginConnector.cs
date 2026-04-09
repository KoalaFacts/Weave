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

    public PluginSchema Schema { get; } = new()
    {
        Type = "webhook",
        Description = "Webhook event bus — publishes domain events via HTTP POST to a URL",
        Provides = ["events"],
        Config =
        [
            new() { Name = "url", Description = "Webhook endpoint URL", Required = true },
        ]
    };

    public Task<PluginStatus> ConnectAsync(string name, PluginDefinition definition)
    {
        var url = definition.Config.GetValueOrDefault("url");
        if (string.IsNullOrWhiteSpace(url))
        {
            return Task.FromResult(new PluginStatus
            {
                Name = name,
                Type = PluginType,
                IsConnected = false,
                Error = "Webhook plugin requires 'url' in config."
            });
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var webhookUri))
        {
            return Task.FromResult(new PluginStatus
            {
                Name = name,
                Type = PluginType,
                IsConnected = false,
                Error = $"Invalid webhook URL: '{url}'"
            });
        }

        var httpClient = httpClientFactory.CreateClient($"webhook-plugin:{name}");
        var eventBus = new WebhookEventBus(
            httpClient,
            webhookUri,
            loggerFactory.CreateLogger<WebhookEventBus>());

        broker.Swap<IEventBus>(eventBus);

        LogWebhookConnected(name, url);

        return Task.FromResult(new PluginStatus
        {
            Name = name,
            Type = PluginType,
            IsConnected = true,
            Info = new Dictionary<string, string> { ["url"] = url }
        });
    }

    public Task<PluginStatus> DisconnectAsync(string name)
    {
        // Only clear the slot if we still own it
        if (broker.Get<IEventBus>() is WebhookEventBus)
            broker.Swap<IEventBus>(null);

        LogWebhookDisconnected(name);
        return Task.FromResult(new PluginStatus { Name = name, Type = PluginType, IsConnected = false });
    }

    public PluginStatus GetStatus(string name)
    {
        var hasBus = broker.Get<IEventBus>() is WebhookEventBus;
        return new PluginStatus { Name = name, Type = PluginType, IsConnected = hasBus };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook plugin '{Name}' connected — posting to {Url}")]
    private partial void LogWebhookConnected(string name, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook plugin '{Name}' disconnected")]
    private partial void LogWebhookDisconnected(string name);
}
