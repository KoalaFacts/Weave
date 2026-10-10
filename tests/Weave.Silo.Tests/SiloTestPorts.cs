using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;

namespace Weave.Silo.Tests;

internal static class SiloTestPorts
{
    public static void Configure(IWebHostBuilder builder) => builder.ConfigureServices(services =>
    {
        using var silo = new TcpListener(IPAddress.Loopback, 0);
        using var gateway = new TcpListener(IPAddress.Loopback, 0);
        silo.Start();
        gateway.Start();
        var siloEndpoint = (IPEndPoint)silo.LocalEndpoint;
        var gatewayEndpoint = (IPEndPoint)gateway.LocalEndpoint;
        siloEndpoint.Port.ShouldNotBe(11111);
        siloEndpoint.Port.ShouldNotBe(30000);
        gatewayEndpoint.Port.ShouldNotBe(11111);
        gatewayEndpoint.Port.ShouldNotBe(30000);
        services.PostConfigure<EndpointOptions>(options =>
        {
            options.AdvertisedIPAddress = IPAddress.Loopback;
            options.SiloPort = siloEndpoint.Port;
            options.GatewayPort = gatewayEndpoint.Port;
            options.SiloListeningEndpoint = siloEndpoint;
            options.GatewayListeningEndpoint = gatewayEndpoint;
        });
        services.PostConfigure<DevelopmentClusterMembershipOptions>(options =>
            options.PrimarySiloEndpoint = siloEndpoint);
    });
}
