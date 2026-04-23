using System.Net;
using System.Net.Http;
using Weave.Agents.Models;
using Weave.Shared.Ids;
using Weave.Silo.Channels;
using static Weave.Silo.Tests.Channels.ChannelAdapterTestHelpers;

namespace Weave.Silo.Tests.Channels;

public sealed class SlackChannelAdapterTests
{
    private static ChannelConfig BuildConfig(string? webhookUrl = "https://hooks.slack.com/services/XYZ")
    {
        var cfg = new Dictionary<string, string>();
        if (webhookUrl is not null)
            cfg["webhook_url"] = webhookUrl;
        return new ChannelConfig
        {
            ChannelId = ChannelId.New(),
            Type = ChannelType.Slack,
            Name = "slack-test",
            Config = cfg
        };
    }

    private static OutboundMessage BuildMessage(string content = "hello slack", string? threadId = null) =>
        new() { ChannelId = ChannelId.New(), Content = content, ThreadId = threadId };

    [Fact]
    public async Task SendAsync_WithValidConfig_PostsJsonToWebhook()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var adapter = new SlackChannelAdapter(http);

        await adapter.SendAsync(BuildMessage("hello slack", "t-123"), BuildConfig(), TestContext.Current.CancellationToken);

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString().ShouldBe("https://hooks.slack.com/services/XYZ");
        AssertJsonPayload(handler.LastRequestBody, json =>
        {
            json.GetProperty("text").GetString().ShouldBe("hello slack");
            json.GetProperty("thread_ts").GetString().ShouldBe("t-123");
        });
    }

    [Fact]
    public async Task SendAsync_MissingWebhookUrl_ThrowsInvalidOperationException()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var adapter = new SlackChannelAdapter(http);

        await Should.ThrowAsync<InvalidOperationException>(
            () => adapter.SendAsync(BuildMessage(), BuildConfig(webhookUrl: null), TestContext.Current.CancellationToken));

        handler.LastRequest.ShouldBeNull("adapter should fail before any HTTP call");
    }

    [Fact]
    public async Task SendAsync_WebhookReturnsErrorStatus_ThrowsHttpRequestException()
    {
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError);
        using var http = new HttpClient(handler);
        var adapter = new SlackChannelAdapter(http);

        await Should.ThrowAsync<HttpRequestException>(
            () => adapter.SendAsync(BuildMessage(), BuildConfig(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidateConfigAsync_WithWebhookUrl_ReturnsTrue()
    {
        var adapter = new SlackChannelAdapter(new HttpClient(new CapturingHandler()));

        var valid = await adapter.ValidateConfigAsync(
            new Dictionary<string, string> { ["webhook_url"] = "https://hooks.slack.com/x" },
            TestContext.Current.CancellationToken);

        valid.ShouldBeTrue();
    }

    [Fact]
    public async Task ValidateConfigAsync_WithoutWebhookUrl_ReturnsFalse()
    {
        var adapter = new SlackChannelAdapter(new HttpClient(new CapturingHandler()));

        var valid = await adapter.ValidateConfigAsync(new Dictionary<string, string>(), TestContext.Current.CancellationToken);

        valid.ShouldBeFalse();
    }

    [Fact]
    public void Type_IsSlack()
    {
        new SlackChannelAdapter(new HttpClient(new CapturingHandler())).Type.ShouldBe(ChannelType.Slack);
    }
}
