using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Weave.Silo.Tests;

[Trait("Category", "Integration")]
public sealed class SiloPortOwnershipTests
{
    [Fact]
    public async Task Startup_ReservesBothOrleansEndpointsBeforeHostedServicesStart_ThenServesHealthAndReleasesPorts()
    {
        var factory = new PortProbeFactory(failBeforeHandoff: false);
        try
        {
            using var client = factory.CreateClient();
            factory.ProbeCompleted.ShouldBeTrue();
            using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await factory.DisposeAsync();
        }

        AssertReleased(factory.Endpoints);
    }

    [Fact]
    public async Task Startup_FailsBeforeListenerHandoff_DisposesBothReservedEndpoints()
    {
        var factory = new PortProbeFactory(failBeforeHandoff: true);
        try
        {
            var failure = Should.Throw<InvalidOperationException>(() => factory.CreateClient());
            failure.Message.ShouldBe(PortProbeFactory.StartupFailure);
            factory.ProbeCompleted.ShouldBeTrue();
            factory.FailedHostDisposed.ShouldBeTrue();
            AssertReleased(factory.Endpoints);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    private static void AssertReleased(IPEndPoint[] endpoints)
    {
        endpoints.Length.ShouldBe(2);
        using var silo = new TcpListener(endpoints[0]) { ExclusiveAddressUse = true };
        using var gateway = new TcpListener(endpoints[1]) { ExclusiveAddressUse = true };
        silo.Start();
        gateway.Start();
        silo.LocalEndpoint.ShouldBe(endpoints[0]);
        gateway.LocalEndpoint.ShouldBe(endpoints[1]);
    }

    private sealed class PortProbeFactory(bool failBeforeHandoff) : WebApplicationFactory<Program>
    {
        public const string StartupFailure = "Owned fixture failure before Orleans listener handoff.";
        private readonly bool _failBeforeHandoff = failBeforeHandoff;
        private readonly string _journal = Directory.CreateTempSubdirectory("weave-port-ownership-").FullName;
        public IPEndPoint[] Endpoints { get; private set; } = [];
        public bool ProbeCompleted { get; private set; }
        public bool FailedHostDisposed { get; private set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            SiloTestPorts.Configure(builder);
            builder.UseSetting("Weave:LocalMode", "true");
            builder.UseSetting("Weave:Storage", "memory");
            builder.UseSetting("Weave:Invocations:DatabasePath", Path.Join(_journal, "invocations.db"));
            builder.UseSetting("urls", "http://127.0.0.1:0");
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ASPNETCORE_ENVIRONMENT"] = "Development" }));
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
            });
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<HostOptions>(options => options.ServicesStartConcurrently = false);
                // Minimal-host applications can start themselves before DeferredHost.Start.
                // Probe first in the real host lifecycle, before Orleans binds its listeners.
                services.Insert(0, ServiceDescriptor.Singleton<IHostedService>(provider =>
                    new PortProbe(provider.GetRequiredService<IOptions<EndpointOptions>>().Value, this)));
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = builder.Build();
            try
            {
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                startup.CancelAfter(TimeSpan.FromSeconds(30));
                host.StartAsync(startup.Token).GetAwaiter().GetResult();
                return host;
            }
            catch
            {
                host.Dispose();
                FailedHostDisposed = true;
                throw;
            }
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (Directory.Exists(_journal))
                Directory.Delete(_journal, recursive: true);
        }

        private sealed class PortProbe(EndpointOptions options, PortProbeFactory owner) : IHostedService
        {
            public Task StartAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.Endpoints = [new(options.AdvertisedIPAddress, options.SiloPort), new(options.AdvertisedIPAddress, options.GatewayPort)];
                owner.Endpoints[0].ShouldNotBe(owner.Endpoints[1]);
                foreach (var endpoint in owner.Endpoints)
                {
                    endpoint.Address.ShouldBe(IPAddress.Loopback);
                    endpoint.Port.ShouldBeGreaterThan(0);
                    endpoint.Port.ShouldNotBe(11111);
                    endpoint.Port.ShouldNotBe(30000);
                    using var competitor = new TcpListener(endpoint) { ExclusiveAddressUse = true };
                    var failure = Should.Throw<SocketException>(() => competitor.Start());
                    failure.SocketErrorCode.ShouldBe(SocketError.AddressAlreadyInUse);
                }
                owner.ProbeCompleted = true;
                if (owner._failBeforeHandoff)
                    throw new InvalidOperationException(StartupFailure);
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
