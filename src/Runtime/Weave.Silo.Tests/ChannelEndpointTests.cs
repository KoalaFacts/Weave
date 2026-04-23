using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Weave.Silo.Tests;

/// <summary>
/// End-to-end channel endpoint tests — register, list, unregister, and
/// inbound message routing. Validation paths covered for register and
/// inbound requests.
/// </summary>
public sealed class ChannelEndpointTests : IClassFixture<SiloFactory>
{
    private readonly SiloFactory _factory;

    public ChannelEndpointTests(SiloFactory factory) => _factory = factory;

    private static string NewWorkspaceId() => $"ws-{Guid.NewGuid():N}";

    private static object BuildRegisterBody(string name = "webhook-1") => new
    {
        Type = "Slack",
        Name = name,
        Config = new Dictionary<string, string> { ["webhook_url"] = "https://hooks.slack.com/services/x" }
    };

    [Fact]
    public async Task List_NewWorkspace_Returns200WithArray()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.GetAsync(
            $"/api/workspaces/{ws}/channels",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldStartWith("[");
    }

    [Fact]
    public async Task Register_WithValidBody_Returns201()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/channels",
            BuildRegisterBody("reg-happy"),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("name").GetString().ShouldBe("reg-happy");
        doc.RootElement.GetProperty("type").GetString().ShouldBe("Slack");
    }

    [Fact]
    public async Task Register_MissingName_Returns400()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/channels",
            new { Type = "Slack", Name = "" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unregister_Returns204()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.DeleteAsync(
            $"/api/workspaces/{ws}/channels/ch_missing",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Inbound_MissingContent_Returns400()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/channels/inbound",
            new
            {
                ChannelId = "ch_1",
                SourceChannel = "Slack",
                SenderId = "u1",
                SenderName = "alice",
                Content = ""
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Inbound_MissingChannelId_Returns400()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/channels/inbound",
            new
            {
                ChannelId = "",
                SourceChannel = "Slack",
                SenderId = "u1",
                SenderName = "alice",
                Content = "hi"
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Inbound_MissingSender_Returns400()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/channels/inbound",
            new
            {
                ChannelId = "ch_1",
                SourceChannel = "Slack",
                SenderId = "",
                SenderName = "alice",
                Content = "hi"
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
