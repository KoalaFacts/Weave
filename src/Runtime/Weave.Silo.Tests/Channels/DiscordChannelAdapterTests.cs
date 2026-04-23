using System.Net;
using System.Net.Http;
using Weave.Agents.Models;
using Weave.Shared.Ids;
using Weave.Silo.Channels;
using static Weave.Silo.Tests.Channels.ChannelAdapterTestHelpers;

namespace Weave.Silo.Tests.Channels;

public sealed class DiscordChannelAdapterTests
{
    private static ChannelConfig BuildConfig(string? webhookUrl = "https://discord.com/api/webhooks/123/abc")
    {
        var cfg = new Dictionary<string, string>();
        if (webhookUrl is not null)
            cfg["webhook_url"] = webhookUrl;
        return new ChannelConfig
        {
            ChannelId = ChannelId.New(),
            Type = ChannelType.Discord,
            Name = "discord-test",
            Config = cfg
        };
    }

    private static OutboundMessage BuildMessage(string content = "hello discord") =>
        new() { ChannelId = ChannelId.New(), Content = content };

    [Fact]
    public async Task SendAsync_WithValidConfig_PostsContentFieldToWebhook()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var adapter = new DiscordChannelAdapter(http);

        await adapter.SendAsync(BuildMessage("hello discord"), BuildConfig(), TestContext.Current.CancellationToken);

        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString().ShouldBe("https://discord.com/api/webhooks/123/abc");
        AssertJsonPayload(handler.LastRequestBody, json =>
            json.GetProperty("content").GetString().ShouldBe("hello discord"));
    }

    [Fact]
    public async Task SendAsync_MissingWebhookUrl_ThrowsInvalidOperationException()
    {
        var adapter = new DiscordChannelAdapter(new HttpClient(new CapturingHandler()));

        await Should.ThrowAsync<InvalidOperationException>(
            () => adapter.SendAsync(BuildMessage(), BuildConfig(webhookUrl: null), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendAsync_WebhookReturnsErrorStatus_ThrowsHttpRequestException()
    {
        using var http = new HttpClient(new CapturingHandler(HttpStatusCode.BadRequest));
        var adapter = new DiscordChannelAdapter(http);

        await Should.ThrowAsync<HttpRequestException>(
            () => adapter.SendAsync(BuildMessage(), BuildConfig(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidateConfigAsync_WithWebhookUrl_ReturnsTrue()
    {
        var adapter = new DiscordChannelAdapter(new HttpClient(new CapturingHandler()));

        var valid = await adapter.ValidateConfigAsync(
            new Dictionary<string, string> { ["webhook_url"] = "https://x" },
            TestContext.Current.CancellationToken);

        valid.ShouldBeTrue();
    }

    [Fact]
    public async Task ValidateConfigAsync_WithoutWebhookUrl_ReturnsFalse()
    {
        var adapter = new DiscordChannelAdapter(new HttpClient(new CapturingHandler()));

        var valid = await adapter.ValidateConfigAsync(new Dictionary<string, string>(), TestContext.Current.CancellationToken);

        valid.ShouldBeFalse();
    }

    [Fact]
    public void Type_IsDiscord()
    {
        new DiscordChannelAdapter(new HttpClient(new CapturingHandler())).Type.ShouldBe(ChannelType.Discord);
    }
}
