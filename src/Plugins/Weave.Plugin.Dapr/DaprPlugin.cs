using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weave.Shared.Events;
using Weave.Shared.Plugins;
using Weave.Tools.Connectors;

[assembly: WeavePlugin(typeof(Weave.Plugin.Dapr.DaprPlugin))]

namespace Weave.Plugin.Dapr;

/// <summary>
/// Provides Dapr pub/sub event bus and Dapr tool connector.
/// Activates only when DAPR_HTTP_PORT is set (i.e., Dapr sidecar is present).
/// </summary>
public sealed class DaprPlugin : IWeavePlugin
{
    public PluginMetadata Metadata { get; } = new("Dapr", "1.0.0", "Dapr sidecar integration for pub/sub events and service invocation tools");

    public bool IsEnabled(PluginContext context) =>
        context.GetValue("DAPR_HTTP_PORT") is not null;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddDaprClient();
        services.AddSingleton<IEventBus, DaprEventBus>();
        services.AddSingleton<IToolConnector>(sp =>
            new DaprToolConnector(
                sp.GetRequiredService<global::Dapr.Client.DaprClient>(),
                sp.GetRequiredService<ILogger<DaprToolConnector>>()));
    }
}
