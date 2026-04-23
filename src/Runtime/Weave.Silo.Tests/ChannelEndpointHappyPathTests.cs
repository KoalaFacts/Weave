using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Weave.Silo.Tests;

/// <summary>
/// Happy-path test for registering and listing a channel. Exercises the
/// <c>RegisterChannelCommand</c> path end-to-end, and the subsequent
/// list returns the registered channel. Complements the validation-only
/// <see cref="ChannelEndpointTests"/>.
/// </summary>
public sealed class ChannelEndpointHappyPathTests : IClassFixture<SiloFactory>
{
    private readonly SiloFactory _factory;

    public ChannelEndpointHappyPathTests(SiloFactory factory) => _factory = factory;

    [Fact]
    public async Task Register_ThenList_ReturnsRegisteredChannel()
    {
        using var client = _factory.CreateClient();
        var ws = $"ws-{Guid.NewGuid():N}";

        using var registerResponse = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/channels",
            new
            {
                Type = "Slack",
                Name = "engineering",
                Config = new Dictionary<string, string> { ["webhook_url"] = "https://hooks.slack.com/x" },
                TargetAgent = (string?)null
            },
            TestContext.Current.CancellationToken);
        var registerBody = await registerResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        registerResponse.StatusCode.ShouldBe(HttpStatusCode.Created, registerBody);

        using var listResponse = await client.GetAsync(
            $"/api/workspaces/{ws}/channels",
            TestContext.Current.CancellationToken);
        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var listBody = await listResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(listBody);
        doc.RootElement.EnumerateArray().Any(c =>
            c.GetProperty("name").GetString() == "engineering").ShouldBeTrue();
    }

    [Fact]
    public async Task RouteInbound_WithRegisteredChannelAndTargetAgent_ReturnsOutbound()
    {
        using var client = _factory.CreateClient();
        var ws = $"ws-{Guid.NewGuid():N}";

        // Activate the target agent first — ChannelGatewayGrain routes
        // inbound messages to the agent via IAgentGrain.SendAsync, so the
        // agent must be Active or the grain call throws.
        using var activateResponse = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/support/activate",
            new { Definition = new { Model = "gpt-4o-mini" } },
            TestContext.Current.CancellationToken);
        activateResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Register a channel that routes to that agent.
        using var registerResponse = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/channels",
            new
            {
                Type = "Slack",
                Name = "support-inbox",
                Config = new Dictionary<string, string> { ["webhook_url"] = "https://hooks/x" },
                TargetAgent = "support"
            },
            TestContext.Current.CancellationToken);
        registerResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var regDoc = JsonDocument.Parse(
            await registerResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var channelId = regDoc.RootElement.GetProperty("channelId").GetString();

        // Route an inbound message through it.
        using var inboundResponse = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/channels/inbound",
            new
            {
                ChannelId = channelId,
                SourceChannel = "Slack",
                SenderId = "u-1",
                SenderName = "alice",
                Content = "hello support"
            },
            TestContext.Current.CancellationToken);

        var inboundBody = await inboundResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        inboundResponse.StatusCode.ShouldBe(HttpStatusCode.OK, inboundBody);
        using var outDoc = JsonDocument.Parse(inboundBody);
        outDoc.RootElement.GetProperty("channelId").GetString().ShouldBe(channelId);
        outDoc.RootElement.GetProperty("content").GetString().ShouldNotBeNullOrEmpty();
    }
}
