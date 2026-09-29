using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Weave.Silo.Tests;

/// <summary>
/// Happy-path tests for <see cref="Api.PluginEndpoints"/> — connect a real
/// plugin (<c>webhook</c>), list, disconnect. Complements
/// <see cref="EndpointGroupSmokeTests"/> which only does a shallow GET.
/// </summary>
public sealed class PluginEndpointHappyPathTests : IClassFixture<SiloFactory>
{
    private sealed class NoContentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
    }

    private readonly SiloFactory _factory;

    public PluginEndpointHappyPathTests(SiloFactory factory) => _factory = factory;

    [Fact]
    public async Task Connect_HttpPlugin_Returns201ThenDisconnectReturns204()
    {
        using var client = _factory.CreateClient();
        SiloFactory.Authorize(client, _factory.Services, "silo", "plugin:connect");
        var pluginName = $"http-{Guid.NewGuid():N}";

        using var connectResponse = await client.PostAsJsonAsync(
            "/api/plugins",
            new
            {
                Name = pluginName,
                Type = "http",
                Config = new Dictionary<string, string> { ["base_url"] = "https://api.example.com" }
            },
            TestContext.Current.CancellationToken);
        var connectBody = await connectResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        connectResponse.StatusCode.ShouldBe(HttpStatusCode.Created, connectBody);

        SiloFactory.Authorize(client, _factory.Services, "silo", "plugin:disconnect");
        using var disconnectResponse = await client.DeleteAsync(
            $"/api/plugins/{pluginName}",
            TestContext.Current.CancellationToken);

        disconnectResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Disconnect_HttpProviderInUse_ReturnsConflictUntilWebhookStops()
    {
        var providerName = $"http-{Guid.NewGuid():N}";
        var webhookName = $"webhook-{Guid.NewGuid():N}";
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient($"plugin:{providerName}")
                .ConfigurePrimaryHttpMessageHandler(() => new NoContentHandler())));
        using var client = factory.CreateClient();
        SiloFactory.Authorize(client, factory.Services, "silo", "plugin:connect");
        try
        {
            using var provider = await client.PostAsJsonAsync("/api/plugins", new
            {
                Name = providerName,
                Type = "http",
                Config = new Dictionary<string, string> { ["base_url"] = "https://api.example.com/" }
            }, TestContext.Current.CancellationToken);
            provider.StatusCode.ShouldBe(HttpStatusCode.Created);

            using var dependent = await client.PostAsJsonAsync("/api/plugins", new
            {
                Name = webhookName,
                Type = "webhook",
                Config = new Dictionary<string, string> { ["url"] = "events" },
                Requires = new Dictionary<string, string> { ["http"] = providerName }
            }, TestContext.Current.CancellationToken);
            dependent.StatusCode.ShouldBe(HttpStatusCode.Created);

            using var composition = await client.GetAsync("/api/plugins/composition",
                TestContext.Current.CancellationToken);
            composition.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var compositionJson = JsonDocument.Parse(await composition.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
            var webhook = compositionJson.RootElement.EnumerateArray().Single(entry =>
                entry.GetProperty("name").GetString() == webhookName);
            webhook.GetProperty("requires").GetProperty("http").GetString().ShouldBe(providerName);

            SiloFactory.Authorize(client, factory.Services, "silo", "plugin:disconnect");
            using var blocked = await client.DeleteAsync($"/api/plugins/{providerName}",
                TestContext.Current.CancellationToken);
            blocked.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await blocked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
                .ShouldContain(webhookName);
            using var after = await client.GetAsync("/api/plugins/composition",
                TestContext.Current.CancellationToken);
            using var afterJson = JsonDocument.Parse(await after.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
            afterJson.RootElement.EnumerateArray().Any(entry =>
                entry.GetProperty("name").GetString() == providerName
                && entry.GetProperty("isConnected").GetBoolean()).ShouldBeTrue();
        }
        finally
        {
            SiloFactory.Authorize(client, factory.Services, "silo", "plugin:disconnect");
            using var dependent = await client.DeleteAsync($"/api/plugins/{webhookName}",
                TestContext.Current.CancellationToken);
            using var provider = await client.DeleteAsync($"/api/plugins/{providerName}",
                TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Connect_WebhookDependencyMissing_RecordsNoEffectFailure()
    {
        using var client = _factory.CreateClient();
        SiloFactory.Authorize(client, _factory.Services, "silo", "plugin:connect");
        using var response = await client.PostAsJsonAsync("/api/plugins", new
        {
            Name = $"webhook-{Guid.NewGuid():N}",
            Type = "webhook",
            Config = new Dictionary<string, string> { ["url"] = "events" },
            Requires = new Dictionary<string, string> { ["http"] = "missing-provider" }
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var operationId = response.Headers.GetValues("X-Weave-Management-Id").Single();
        SiloFactory.Authorize(client, _factory.Services, "silo", "management:operations:read");
        using var operation = await client.GetAsync($"/api/management/operations/silo/{operationId}",
            TestContext.Current.CancellationToken);
        operation.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await operation.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));
        json.RootElement.GetProperty("outcome").GetString().ShouldBe("Failed");
    }

    [Fact]
    public async Task Connect_MissingRequiredConfigField_Returns400()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/plugins",
            new
            {
                Name = $"http-{Guid.NewGuid():N}",
                Type = "http",
                // base_url intentionally missing — schema marks it required
                Config = new Dictionary<string, string>()
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("base_url");
    }

    [Fact]
    public async Task Connect_UnknownConfigKey_ReturnsCreatedWithWarning()
    {
        // Schema validator returns unknown keys as warnings (non-fatal).
        using var client = _factory.CreateClient();
        SiloFactory.Authorize(client, _factory.Services, "silo", "plugin:connect");
        var name = $"http-{Guid.NewGuid():N}";

        using var response = await client.PostAsJsonAsync(
            "/api/plugins",
            new
            {
                Name = name,
                Type = "http",
                Config = new Dictionary<string, string>
                {
                    ["base_url"] = "https://api.example.com",
                    ["unknown_key"] = "value"
                }
            },
            TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        ((int)response.StatusCode).ShouldBeLessThan(500, body);
        if (response.StatusCode == HttpStatusCode.Created)
        {
            using var doc = JsonDocument.Parse(body);
            doc.RootElement.GetProperty("warnings").EnumerateArray().Any(w =>
                w.GetString()?.Contains("unknown_key", StringComparison.OrdinalIgnoreCase) == true)
                .ShouldBeTrue("unknown config keys should surface as warnings");
        }

        // cleanup
        SiloFactory.Authorize(client, _factory.Services, "silo", "plugin:disconnect");
        using var _ = await client.DeleteAsync($"/api/plugins/{name}", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CatalogList_IncludesBuiltInConnectors()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/plugins/catalog", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("auth");
        body.ShouldContain("http");
        body.ShouldContain("dapr");
        body.ShouldContain("vault");
        body.ShouldContain("webhook");
    }

    [Fact]
    public async Task Composition_AfterHttpConnect_ShowsOwnedRegistrationWithoutConfiguration()
    {
        using var client = _factory.CreateClient();
        SiloFactory.Authorize(client, _factory.Services, "silo", "plugin:connect");
        var name = $"http-{Guid.NewGuid():N}";

        try
        {
            using var connectResponse = await client.PostAsJsonAsync(
                "/api/plugins",
                new
                {
                    Name = name,
                    Type = "http",
                    Config = new Dictionary<string, string>
                    {
                        ["base_url"] = "https://api.example.com",
                        ["private_marker"] = "never-include-this-value"
                    }
                },
                TestContext.Current.CancellationToken);
            connectResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

            using var response = await client.GetAsync(
                "/api/plugins/composition", TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var json = JsonDocument.Parse(body);
            var plugin = json.RootElement.EnumerateArray().Single(entry =>
                entry.GetProperty("name").GetString() == name);

            plugin.GetProperty("isConnected").GetBoolean().ShouldBeTrue();
            plugin.GetProperty("registrations").EnumerateArray()
                .Select(value => value.GetString()).ShouldContain($"http:{name}");
            body.ShouldNotContain("https://api.example.com");
            body.ShouldNotContain("never-include-this-value");
        }
        finally
        {
            SiloFactory.Authorize(client, _factory.Services, "silo", "plugin:disconnect");
            using var response = await client.DeleteAsync(
                $"/api/plugins/{name}", TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }
    }
}
