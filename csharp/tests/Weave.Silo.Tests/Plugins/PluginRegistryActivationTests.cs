using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Plugins;
using Weave.Silo.Events;
using Weave.Silo.Plugins;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;

namespace Weave.Silo.Tests.Plugins;

public sealed class PluginRegistryActivationTests
{
    private sealed record TestEvent : IDomainEvent
    {
        public string EventId { get; init; } = "dependency-event";
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UnixEpoch;
        public string SourceId { get; init; } = "test";
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private static readonly CapabilityTokenService TokenService = new(
        Options.Create(new CapabilityTokenOptions { SigningKey = "test-signing-key-that-is-at-least-32-chars-long" }),
        TimeProvider.System);

    private static readonly CapabilityToken AnyPlugin = TokenService.Mint(new CapabilityTokenRequest
    {
        WorkspaceId = "test",
        IssuedTo = "test",
        Grants = ["plugin:invoke:*"],
        Lifetime = TimeSpan.FromHours(1)
    });

    private static PluginRegistry CreateRegistry(params IPluginConnector[] connectors) =>
        new(connectors,
            new CapabilityAuthorizer(TokenService, Substitute.For<IEventBus>(), NullLogger<CapabilityAuthorizer>.Instance),
            new InstallationDiagnostics(TimeProvider.System),
            NullLogger<PluginRegistry>.Instance);

    private static ServiceProvider CreateHttpServices() =>
        new ServiceCollection().AddHttpClient().BuildServiceProvider();

    private static PluginDefinition DependentWebhook() => new()
    {
        Type = "webhook",
        Config = new Dictionary<string, string> { ["url"] = "events" },
        Requires = new Dictionary<string, string> { ["http"] = "api" }
    };

    [Fact]
    public async Task ConnectAsync_WebhookDependencyMissing_LeavesEventBusUnchanged()
    {
        var broker = new PluginServiceBroker(NullLogger<PluginServiceBroker>.Instance);
        using var services = CreateHttpServices();
        using var registry = CreateRegistry(new WebhookPluginConnector(
            broker, services.GetRequiredService<IHttpClientFactory>(), NullLoggerFactory.Instance));

        var status = await registry.ConnectAsync("events", DependentWebhook(), AnyPlugin);

        status.IsConnected.ShouldBeFalse();
        status.Error.ShouldNotBeNull();
        status.Error.ShouldContain("api");
        broker.Get<IEventBus>().ShouldBeNull();
    }

    [Fact]
    public async Task ConnectAsync_WebhookDependency_UsesProviderAndProtectsLifecycle()
    {
        var broker = new PluginServiceBroker(NullLogger<PluginServiceBroker>.Instance);
        var handler = new CapturingHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));
        using var registry = CreateRegistry(
            new HttpPluginConnector(broker, factory, NullLoggerFactory.Instance),
            new WebhookPluginConnector(broker, factory, NullLoggerFactory.Instance));

        (await registry.ConnectAsync("api", new PluginDefinition
        {
            Type = "http",
            Config = new Dictionary<string, string> { ["base_url"] = "https://api.example/" }
        }, AnyPlugin)).IsConnected.ShouldBeTrue();
        var providerClient = broker.Get<HttpClient>("http:api");

        (await registry.ConnectAsync("events", DependentWebhook(), AnyPlugin)).IsConnected.ShouldBeTrue();
        await broker.Get<IEventBus>()!.PublishAsync(new TestEvent(), TestContext.Current.CancellationToken);
        handler.LastRequestUri.ShouldBe(new Uri("https://api.example/events"));
        var composition = await registry.GetCompositionAsync(TestContext.Current.CancellationToken);
        composition.Single(item => item.Name == "events").Requires["http"].ShouldBe("api");

        var disconnect = await registry.DisconnectAsync("api", AnyPlugin);
        disconnect.IsConnected.ShouldBeTrue();
        disconnect.Error.ShouldNotBeNull();
        disconnect.Error.ShouldContain("events");
        broker.Get<HttpClient>("http:api").ShouldBeSameAs(providerClient);

        var replacement = await registry.ConnectAsync("api", new PluginDefinition
        {
            Type = "http",
            Config = new Dictionary<string, string> { ["base_url"] = "https://other.example/" }
        }, AnyPlugin);
        replacement.IsConnected.ShouldBeFalse();
        broker.Get<HttpClient>("http:api").ShouldBeSameAs(providerClient);

        (await registry.DisconnectAsync("events", AnyPlugin)).Error.ShouldBeNull();
        (await registry.DisconnectAsync("api", AnyPlugin)).Error.ShouldBeNull();
        broker.Get<HttpClient>("http:api").ShouldBeNull();
    }

    [Fact]
    public async Task ConnectAllAsync_DependentListedFirst_ActivatesAfterProvider()
    {
        var broker = new PluginServiceBroker(NullLogger<PluginServiceBroker>.Instance);
        using var services = CreateHttpServices();
        var factory = services.GetRequiredService<IHttpClientFactory>();
        using var registry = CreateRegistry(
            new HttpPluginConnector(broker, factory, NullLoggerFactory.Instance),
            new WebhookPluginConnector(broker, factory, NullLoggerFactory.Instance));
        var plugins = new Dictionary<string, PluginDefinition>
        {
            ["events"] = DependentWebhook(),
            ["api"] = new()
            {
                Type = "http",
                Config = new Dictionary<string, string> { ["base_url"] = "https://api.example/" }
            }
        };

        var statuses = await registry.ConnectAllAsync(plugins, AnyPlugin);

        statuses.Select(item => item.Name).ShouldBe(["events", "api"]);
        statuses.ShouldAllBe(item => item.IsConnected);
        broker.Get<IEventBus>().ShouldBeOfType<WebhookEventBus>();
    }

    [Fact]
    public async Task ConnectAsync_UnsupportedDependency_RejectsWithoutActivation()
    {
        var broker = new PluginServiceBroker(NullLogger<PluginServiceBroker>.Instance);
        using var services = CreateHttpServices();
        using var registry = CreateRegistry(new HttpPluginConnector(
            broker, services.GetRequiredService<IHttpClientFactory>(), NullLoggerFactory.Instance));

        var status = await registry.ConnectAsync("api", new PluginDefinition
        {
            Type = "http",
            Config = new Dictionary<string, string> { ["base_url"] = "https://api.example/" },
            Requires = new Dictionary<string, string> { ["secrets"] = "vault" }
        }, AnyPlugin);

        status.IsConnected.ShouldBeFalse();
        status.Error.ShouldNotBeNull();
        status.Error.ShouldContain("does not consume");
        broker.Get<HttpClient>("http:api").ShouldBeNull();
    }

    [Theory]
    [InlineData("//other.example/events")]
    [InlineData("\\\\other.example/events")]
    public async Task ConnectAsync_WebhookDependencyCrossOrigin_Rejects(string relativeUrl)
    {
        var broker = new PluginServiceBroker(NullLogger<PluginServiceBroker>.Instance);
        using var services = CreateHttpServices();
        var factory = services.GetRequiredService<IHttpClientFactory>();
        using var registry = CreateRegistry(
            new HttpPluginConnector(broker, factory, NullLoggerFactory.Instance),
            new WebhookPluginConnector(broker, factory, NullLoggerFactory.Instance));
        (await registry.ConnectAsync("api", new PluginDefinition
        {
            Type = "http",
            Config = new Dictionary<string, string> { ["base_url"] = "https://api.example/" }
        }, AnyPlugin)).IsConnected.ShouldBeTrue();

        var status = await registry.ConnectAsync("events", DependentWebhook() with
        {
            Config = new Dictionary<string, string> { ["url"] = relativeUrl }
        }, AnyPlugin);

        status.IsConnected.ShouldBeFalse();
        broker.Get<IEventBus>().ShouldBeNull();
    }

    [Fact]
    public async Task ConnectAsync_ReplacingHttpPlugin_KeepsReplacementRegistered()
    {
        var broker = new PluginServiceBroker(NullLogger<PluginServiceBroker>.Instance);
        using var services = CreateHttpServices();
        using var registry = CreateRegistry(new HttpPluginConnector(
            broker, services.GetRequiredService<IHttpClientFactory>(), NullLoggerFactory.Instance));

        await registry.ConnectAsync("api", new PluginDefinition
        {
            Type = "http",
            Config = new Dictionary<string, string> { ["base_url"] = "https://first.example" }
        }, AnyPlugin);
        var first = broker.Get<HttpClient>("http:api");

        var status = await registry.ConnectAsync("api", new PluginDefinition
        {
            Type = "http",
            Config = new Dictionary<string, string> { ["base_url"] = "https://second.example" }
        }, AnyPlugin);

        status.IsConnected.ShouldBeTrue();
        var current = broker.Get<HttpClient>("http:api");
        current.ShouldNotBeNull();
        current.ShouldNotBeSameAs(first);
        current.BaseAddress.ShouldBe(new Uri("https://second.example"));
    }

    [Fact]
    public async Task ConnectAsync_CompetingEventProviders_LeavesFirstProviderActive()
    {
        var broker = new PluginServiceBroker(NullLogger<PluginServiceBroker>.Instance);
        using var services = CreateHttpServices();
        using var registry = CreateRegistry(new WebhookPluginConnector(
            broker, services.GetRequiredService<IHttpClientFactory>(), NullLoggerFactory.Instance));

        var first = await registry.ConnectAsync("first", new PluginDefinition
        {
            Type = "webhook",
            Config = new Dictionary<string, string> { ["url"] = "https://first.example/events" }
        }, AnyPlugin);
        var firstBus = broker.Get<IEventBus>();

        var second = await registry.ConnectAsync("second", new PluginDefinition
        {
            Type = "webhook",
            Config = new Dictionary<string, string> { ["url"] = "https://second.example/events" }
        }, AnyPlugin);

        first.IsConnected.ShouldBeTrue();
        second.IsConnected.ShouldBeFalse();
        broker.Get<IEventBus>().ShouldBeSameAs(firstBus);
        registry.GetAll().Select(x => x.Name).ShouldBe(["first"]);
    }

    [Fact]
    public async Task ConnectAsync_ReplacingDaprPlugin_PreservesNewToolRegistration()
    {
        var broker = new PluginServiceBroker(NullLogger<PluginServiceBroker>.Instance);
        var discovery = new ToolDiscoveryService([], NullLogger<ToolDiscoveryService>.Instance);
        using var services = CreateHttpServices();
        using var registry = CreateRegistry(new DaprPluginConnector(
            broker, discovery, services.GetRequiredService<IHttpClientFactory>(), NullLoggerFactory.Instance));

        await registry.ConnectAsync("sidecar", new PluginDefinition
        {
            Type = "dapr",
            Config = new Dictionary<string, string> { ["port"] = "3500" }
        }, AnyPlugin);
        var first = discovery.GetConnector(ToolType.Dapr);

        var status = await registry.ConnectAsync("sidecar", new PluginDefinition
        {
            Type = "dapr",
            Config = new Dictionary<string, string> { ["port"] = "3501" }
        }, AnyPlugin);

        status.IsConnected.ShouldBeTrue();
        discovery.GetConnector(ToolType.Dapr).ShouldNotBeSameAs(first);
        broker.Get<IEventBus>().ShouldNotBeNull();

        await registry.DisconnectAsync("sidecar", AnyPlugin);
        Should.Throw<NotSupportedException>(() => discovery.GetConnector(ToolType.Dapr));
        broker.Get<IEventBus>().ShouldBeNull();
    }

    [Fact]
    public async Task ConnectAsync_DaprRegistrationFails_RestoresPreviousEventBus()
    {
        var broker = new PluginServiceBroker(NullLogger<PluginServiceBroker>.Instance);
        var discovery = Substitute.For<IToolDiscoveryService>();
        var registrations = 0;
        discovery.When(service => service.Register(Arg.Any<IToolConnector>()))
            .Do(_ =>
            {
                if (++registrations == 2)
                    throw new InvalidOperationException("registration failed");
            });
        using var services = CreateHttpServices();
        using var registry = CreateRegistry(new DaprPluginConnector(
            broker, discovery, services.GetRequiredService<IHttpClientFactory>(), NullLoggerFactory.Instance));

        var first = await registry.ConnectAsync("sidecar", new PluginDefinition
        {
            Type = "dapr",
            Config = new Dictionary<string, string> { ["port"] = "3500" }
        }, AnyPlugin);
        first.IsConnected.ShouldBeTrue();
        var previousBus = broker.Get<IEventBus>();

        var replacement = await registry.ConnectAsync("sidecar", new PluginDefinition
        {
            Type = "dapr",
            Config = new Dictionary<string, string> { ["port"] = "3501" }
        }, AnyPlugin);

        replacement.IsConnected.ShouldBeFalse();
        broker.Get<IEventBus>().ShouldBeSameAs(previousBus);
        registry.GetAll().Single().Type.ShouldBe("dapr");
    }

    [Fact]
    public async Task ConnectAsync_ReplacingWebhookWithDapr_KeepsNewEventBus()
    {
        var broker = new PluginServiceBroker(NullLogger<PluginServiceBroker>.Instance);
        var discovery = new ToolDiscoveryService([], NullLogger<ToolDiscoveryService>.Instance);
        using var services = CreateHttpServices();
        var clientFactory = services.GetRequiredService<IHttpClientFactory>();
        using var registry = CreateRegistry(
            new WebhookPluginConnector(broker, clientFactory, NullLoggerFactory.Instance),
            new DaprPluginConnector(broker, discovery, clientFactory, NullLoggerFactory.Instance));

        var first = await registry.ConnectAsync("events", new PluginDefinition
        {
            Type = "webhook",
            Config = new Dictionary<string, string> { ["url"] = "https://first.example/events" }
        }, AnyPlugin);
        first.IsConnected.ShouldBeTrue();
        var previous = broker.Get<IEventBus>();
        previous.ShouldBeOfType<WebhookEventBus>();

        var replacement = await registry.ConnectAsync("events", new PluginDefinition
        {
            Type = "dapr",
            Config = new Dictionary<string, string> { ["port"] = "3500" }
        }, AnyPlugin);

        replacement.IsConnected.ShouldBeTrue();
        broker.Get<IEventBus>().ShouldNotBeSameAs(previous);
        discovery.GetConnector(ToolType.Dapr).ShouldBeOfType<DaprToolConnector>();
        registry.GetAll().Single().Type.ShouldBe("dapr");
    }

    [Fact]
    public async Task Dispose_ActiveHttpPlugin_RemovesOwnedRegistration()
    {
        var broker = new PluginServiceBroker(NullLogger<PluginServiceBroker>.Instance);
        using var services = CreateHttpServices();
        using var registry = CreateRegistry(new HttpPluginConnector(
            broker, services.GetRequiredService<IHttpClientFactory>(), NullLoggerFactory.Instance));

        await registry.ConnectAsync("api", new PluginDefinition
        {
            Type = "http",
            Config = new Dictionary<string, string> { ["base_url"] = "https://api.example" }
        }, AnyPlugin);
        broker.Get<HttpClient>("http:api").ShouldNotBeNull();

        registry.Dispose();

        broker.Get<HttpClient>("http:api").ShouldBeNull();
    }
}
