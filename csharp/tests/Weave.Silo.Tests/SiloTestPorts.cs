using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;

namespace Weave.Silo.Tests;

internal static class SiloTestPorts
{
    public static void Configure(IWebHostBuilder builder) => builder.ConfigureServices(services =>
    {
        // Preserve Orleans' opaque transport keys and native listener factories.
        var transports = services.Where(descriptor => descriptor.IsKeyedService
            && descriptor.ServiceType == typeof(IConnectionListenerFactory)).ToArray();
        transports.Length.ShouldBe(2, "Orleans 10.1 registers silo and gateway listener factories.");
        foreach (var descriptor in transports)
        {
            descriptor.Lifetime.ShouldBe(ServiceLifetime.Singleton);
            descriptor.KeyedImplementationFactory.ShouldNotBeNull();
        }
        services.AddSingleton(provider => new SiloTestListeners(
            (IConnectionListenerFactory)transports[0].KeyedImplementationFactory!(provider, transports[0].ServiceKey),
            (IConnectionListenerFactory)transports[1].KeyedImplementationFactory!(provider, transports[1].ServiceKey)));
        foreach (var descriptor in transports)
        {
            services.Remove(descriptor);
            services.Add(ServiceDescriptor.KeyedSingleton<IConnectionListenerFactory>(descriptor.ServiceKey,
                (provider, _) => provider.GetRequiredService<SiloTestListeners>()));
        }
        services.AddOptions<EndpointOptions>().PostConfigure<SiloTestListeners>((options, listeners) =>
        {
            options.AdvertisedIPAddress = IPAddress.Loopback;
            options.SiloPort = listeners.SiloEndpoint.Port;
            options.GatewayPort = listeners.GatewayEndpoint.Port;
            options.SiloListeningEndpoint = listeners.SiloEndpoint;
            options.GatewayListeningEndpoint = listeners.GatewayEndpoint;
        });
        services.AddOptions<DevelopmentClusterMembershipOptions>().PostConfigure<SiloTestListeners>(
            (options, listeners) => options.PrimarySiloEndpoint = listeners.SiloEndpoint);
    });
}
