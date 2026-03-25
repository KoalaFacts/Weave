using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weave.Shared.Events;
using Weave.Silo.Events;
using Weave.Workspaces.Models;
using Weave.Workspaces.Plugins;

namespace Weave.Silo.Plugins;

/// <summary>
/// Connects the webhook plugin — registers <see cref="WebhookEventBus"/>
/// that publishes domain events via plain HTTP POST. No sidecar or message broker required.
/// </summary>
public sealed partial class WebhookPluginConnector(
    IServiceCollection services,
    ILogger<WebhookPluginConnector> logger) : IPluginConnector
{
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

        services.AddHttpClient<WebhookEventBus>();
        services.AddSingleton<IEventBus>(sp =>
            new WebhookEventBus(
                sp.GetRequiredService<HttpClient>(),
                webhookUri,
                sp.GetRequiredService<ILogger<WebhookEventBus>>()));

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
        return new PluginStatus { Name = name, Type = PluginType, IsConnected = false };
    }

    public PluginStatus GetStatus(string name)
    {
        return new PluginStatus { Name = name, Type = PluginType, IsConnected = true };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook plugin '{Name}' connected — posting to {Url}")]
    private partial void LogWebhookConnected(string name, string url);
}
