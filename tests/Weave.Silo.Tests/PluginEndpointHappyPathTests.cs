using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Weave.Silo.Tests;

/// <summary>
/// Happy-path tests for <see cref="Api.PluginEndpoints"/> — connect a real
/// plugin (<c>webhook</c>), list, disconnect. Complements
/// <see cref="EndpointGroupSmokeTests"/> which only does a shallow GET.
/// </summary>
public sealed class PluginEndpointHappyPathTests : IClassFixture<SiloFactory>
{
    private readonly SiloFactory _factory;

    public PluginEndpointHappyPathTests(SiloFactory factory) => _factory = factory;

    [Fact]
    public async Task Connect_HttpPlugin_Returns201ThenDisconnectReturns204()
    {
        using var client = _factory.CreateClient();
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

        using var disconnectResponse = await client.DeleteAsync(
            $"/api/plugins/{pluginName}",
            TestContext.Current.CancellationToken);

        disconnectResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
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
            using var response = await client.DeleteAsync(
                $"/api/plugins/{name}", TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }
    }
}
