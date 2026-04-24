using System.Net;

namespace Weave.Silo.Tests;

/// <summary>
/// Endpoint-group smoke tests — one hit per group to prove the routes
/// are mapped and the handlers don't throw on a trivial input. Matches
/// the "every endpoint group mapped in Program.cs has at least one
/// SiloFactory-based test" rule in docs/best-practices.md.
///
/// These are cheap: they do NOT exercise the happy path for each group
/// (that's the job of dedicated *LifecycleTests files). They exist to
/// catch regressions where a new endpoint breaks routing, DI, or JSON
/// serialization before it reaches the CLI / Dashboard.
/// </summary>
public sealed class EndpointGroupSmokeTests : IClassFixture<SiloFactory>
{
    private readonly SiloFactory _factory;

    public EndpointGroupSmokeTests(SiloFactory factory) => _factory = factory;

    [Fact]
    public async Task Templates_list_returns_200_and_json_array()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/templates", TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        body.ShouldStartWith("[");
    }

    [Fact]
    public async Task Plugins_list_returns_200_and_does_not_throw()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/plugins", TestContext.Current.CancellationToken);

        // Plugins list may be empty, but the endpoint must be wired
        // and succeed — covers the env-detected plugin registration
        // (Dapr/Vault) that Program.cs builds at startup.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Marketplace_list_returns_200_and_json_array()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/marketplace", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldStartWith("[");
    }

    [Fact]
    public async Task Agents_list_on_unknown_workspace_does_not_500()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/workspaces/no-such-workspace/agents",
            TestContext.Current.CancellationToken);

        // The current actor implementation activates on-demand and
        // returns an empty list for an unknown workspace — that's fine.
        // What we want to guarantee is that the endpoint does not
        // crash with a 500 (the actual failure shape the 409 session
        // bug produced).
        ((int)response.StatusCode).ShouldBeLessThan(500);
    }

    [Fact]
    public async Task Tools_list_on_unknown_workspace_does_not_500()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/workspaces/no-such-workspace/tools",
            TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        ((int)response.StatusCode).ShouldBeLessThan(500, body);
    }

    [Fact]
    public async Task Skills_list_on_unknown_workspace_does_not_500()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/workspaces/no-such-workspace/skills",
            TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        ((int)response.StatusCode).ShouldBeLessThan(500, body);
    }

    [Fact]
    public async Task Channels_list_on_unknown_workspace_does_not_500()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/workspaces/no-such-workspace/channels",
            TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        ((int)response.StatusCode).ShouldBeLessThan(500, body);
    }

    [Fact]
    public async Task Users_profile_on_unknown_workspace_does_not_500()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/workspaces/no-such-workspace/users/test-user/profile",
            TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        ((int)response.StatusCode).ShouldBeLessThan(500, body);
    }
}
