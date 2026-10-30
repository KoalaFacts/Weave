using System.Net;
using Weave.Agents.Channels;
using Weave.Shared.Ids;
using Weave.Silo.Channels;
using static Weave.Silo.Tests.Channels.ChannelAdapterTestHelpers;

namespace Weave.Silo.Tests.Channels;

public sealed class TelegramChannelAdapterTests
{
    private static ChannelConfig BuildConfig(string? botToken = "BOT123", string? chatId = "CHAT42")
    {
        var cfg = new Dictionary<string, string>();
        if (botToken is not null)
            cfg["bot_token"] = botToken;
        if (chatId is not null)
            cfg["chat_id"] = chatId;
        return new ChannelConfig
        {
            ChannelId = ChannelId.New(),
            Type = ChannelType.Telegram,
            Name = "tg-test",
            Config = cfg
        };
    }

    private static OutboundMessage BuildMessage(string content = "hello tg", string? threadId = null) =>
        new() { ChannelId = ChannelId.New(), Content = content, ThreadId = threadId };

    [Fact]
    public async Task SendAsync_WithValidConfig_PostsToBotEndpointWithChatAndText()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var adapter = new TelegramChannelAdapter(http);

        await adapter.SendAsync(BuildMessage("hello tg", "99"), BuildConfig(), TestContext.Current.CancellationToken);

        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString().ShouldBe("https://api.telegram.org/botBOT123/sendMessage");
        AssertJsonPayload(handler.LastRequestBody, json =>
        {
            json.GetProperty("chat_id").GetString().ShouldBe("CHAT42");
            json.GetProperty("text").GetString().ShouldBe("hello tg");
            json.GetProperty("reply_to_message_id").GetString().ShouldBe("99");
        });
    }

    [Fact]
    public async Task SendAsync_MissingBotToken_ThrowsInvalidOperationException()
    {
        var adapter = new TelegramChannelAdapter(new HttpClient(new CapturingHandler()));

        await Should.ThrowAsync<InvalidOperationException>(
            () => adapter.SendAsync(BuildMessage(), BuildConfig(botToken: null), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendAsync_MissingChatId_ThrowsInvalidOperationException()
    {
        var adapter = new TelegramChannelAdapter(new HttpClient(new CapturingHandler()));

        await Should.ThrowAsync<InvalidOperationException>(
            () => adapter.SendAsync(BuildMessage(), BuildConfig(chatId: null), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendAsync_TelegramReturnsErrorStatus_ThrowsHttpRequestException()
    {
        using var http = new HttpClient(new CapturingHandler(HttpStatusCode.Unauthorized));
        var adapter = new TelegramChannelAdapter(http);

        await Should.ThrowAsync<HttpRequestException>(
            () => adapter.SendAsync(BuildMessage(), BuildConfig(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidateConfigAsync_WithBothFields_ReturnsTrue()
    {
        var adapter = new TelegramChannelAdapter(new HttpClient(new CapturingHandler()));

        var valid = await adapter.ValidateConfigAsync(
            new Dictionary<string, string> { ["bot_token"] = "t", ["chat_id"] = "c" },
            TestContext.Current.CancellationToken);

        valid.ShouldBeTrue();
    }

    [Fact]
    public async Task ValidateConfigAsync_WithOnlyBotToken_ReturnsFalse()
    {
        var adapter = new TelegramChannelAdapter(new HttpClient(new CapturingHandler()));

        var valid = await adapter.ValidateConfigAsync(
            new Dictionary<string, string> { ["bot_token"] = "t" },
            TestContext.Current.CancellationToken);

        valid.ShouldBeFalse();
    }

    [Fact]
    public void Type_IsTelegram()
    {
        new TelegramChannelAdapter(new HttpClient(new CapturingHandler())).Type.ShouldBe(ChannelType.Telegram);
    }
}
