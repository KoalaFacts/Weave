using System.Net;
using System.Net.Http;
using Weave.Agents.Models;
using Weave.Shared.Ids;
using Weave.Silo.Channels;
using static Weave.Silo.Tests.Channels.ChannelAdapterTestHelpers;

namespace Weave.Silo.Tests.Channels;

public sealed class TeamsChannelAdapterTests
{
    private static ChannelConfig BuildConfig(string? webhookUrl = "https://outlook.office.com/webhook/1")
    {
        var cfg = new Dictionary<string, string>();
        if (webhookUrl is not null)
            cfg["webhook_url"] = webhookUrl;
        return new ChannelConfig
        {
            ChannelId = ChannelId.New(),
            Type = ChannelType.Teams,
            Name = "teams-test",
            Config = cfg
        };
    }

    private static OutboundMessage BuildMessage(string content = "hello teams") =>
        new() { ChannelId = ChannelId.New(), Content = content };

    [Fact]
    public async Task SendAsync_WithValidConfig_PostsTextFieldToWebhook()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var adapter = new TeamsChannelAdapter(http);

        await adapter.SendAsync(BuildMessage("hello teams"), BuildConfig(), TestContext.Current.CancellationToken);

        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString().ShouldBe("https://outlook.office.com/webhook/1");
        AssertJsonPayload(handler.LastRequestBody, json =>
            json.GetProperty("text").GetString().ShouldBe("hello teams"));
    }

    [Fact]
    public async Task SendAsync_MissingWebhookUrl_ThrowsInvalidOperationException()
    {
        var adapter = new TeamsChannelAdapter(new HttpClient(new CapturingHandler()));

        await Should.ThrowAsync<InvalidOperationException>(
            () => adapter.SendAsync(BuildMessage(), BuildConfig(webhookUrl: null), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendAsync_WebhookReturnsErrorStatus_ThrowsHttpRequestException()
    {
        using var http = new HttpClient(new CapturingHandler(HttpStatusCode.ServiceUnavailable));
        var adapter = new TeamsChannelAdapter(http);

        await Should.ThrowAsync<HttpRequestException>(
            () => adapter.SendAsync(BuildMessage(), BuildConfig(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidateConfigAsync_WithWebhookUrl_ReturnsTrue()
    {
        var adapter = new TeamsChannelAdapter(new HttpClient(new CapturingHandler()));

        var valid = await adapter.ValidateConfigAsync(
            new Dictionary<string, string> { ["webhook_url"] = "https://x" },
            TestContext.Current.CancellationToken);

        valid.ShouldBeTrue();
    }

    [Fact]
    public async Task ValidateConfigAsync_WithBlankWebhookUrl_ReturnsFalse()
    {
        var adapter = new TeamsChannelAdapter(new HttpClient(new CapturingHandler()));

        var valid = await adapter.ValidateConfigAsync(
            new Dictionary<string, string> { ["webhook_url"] = "   " },
            TestContext.Current.CancellationToken);

        valid.ShouldBeFalse();
    }

    [Fact]
    public void Type_IsTeams()
    {
        new TeamsChannelAdapter(new HttpClient(new CapturingHandler())).Type.ShouldBe(ChannelType.Teams);
    }
}
