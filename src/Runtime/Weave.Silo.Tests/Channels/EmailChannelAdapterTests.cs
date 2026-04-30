using System.Net;
using Weave.Agents.Models;
using Weave.Shared.Ids;
using Weave.Silo.Channels;
using static Weave.Silo.Tests.Channels.ChannelAdapterTestHelpers;

namespace Weave.Silo.Tests.Channels;

public sealed class EmailChannelAdapterTests
{
    private static ChannelConfig BuildConfig(
        string? relayUrl = "https://relay.example.com/send",
        string? to = "recipient@example.com",
        string? subject = null)
    {
        var cfg = new Dictionary<string, string>();
        if (relayUrl is not null)
            cfg["relay_url"] = relayUrl;
        if (to is not null)
            cfg["to"] = to;
        if (subject is not null)
            cfg["subject"] = subject;
        return new ChannelConfig
        {
            ChannelId = ChannelId.New(),
            Type = ChannelType.Email,
            Name = "email-test",
            Config = cfg
        };
    }

    private static OutboundMessage BuildMessage(string content = "hello email") =>
        new() { ChannelId = ChannelId.New(), Content = content };

    [Fact]
    public async Task SendAsync_WithValidConfig_PostsToRelayUrlWithDefaultSubject()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var adapter = new EmailChannelAdapter(http);

        await adapter.SendAsync(BuildMessage("hello email"), BuildConfig(), TestContext.Current.CancellationToken);

        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString().ShouldBe("https://relay.example.com/send");
        AssertJsonPayload(handler.LastRequestBody, json =>
        {
            json.GetProperty("to").GetString().ShouldBe("recipient@example.com");
            json.GetProperty("subject").GetString().ShouldBe("Weave Agent Response");
            json.GetProperty("body").GetString().ShouldBe("hello email");
        });
    }

    [Fact]
    public async Task SendAsync_WithCustomSubject_UsesConfiguredSubject()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var adapter = new EmailChannelAdapter(http);

        await adapter.SendAsync(
            BuildMessage(),
            BuildConfig(subject: "Custom Subject"),
            TestContext.Current.CancellationToken);

        AssertJsonPayload(handler.LastRequestBody, json =>
            json.GetProperty("subject").GetString().ShouldBe("Custom Subject"));
    }

    [Fact]
    public async Task SendAsync_MissingRelayUrl_ThrowsInvalidOperationException()
    {
        var adapter = new EmailChannelAdapter(new HttpClient(new CapturingHandler()));

        await Should.ThrowAsync<InvalidOperationException>(
            () => adapter.SendAsync(BuildMessage(), BuildConfig(relayUrl: null), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendAsync_MissingTo_ThrowsInvalidOperationException()
    {
        var adapter = new EmailChannelAdapter(new HttpClient(new CapturingHandler()));

        await Should.ThrowAsync<InvalidOperationException>(
            () => adapter.SendAsync(BuildMessage(), BuildConfig(to: null), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendAsync_RelayReturnsErrorStatus_ThrowsHttpRequestException()
    {
        using var http = new HttpClient(new CapturingHandler(HttpStatusCode.BadGateway));
        var adapter = new EmailChannelAdapter(http);

        await Should.ThrowAsync<HttpRequestException>(
            () => adapter.SendAsync(BuildMessage(), BuildConfig(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidateConfigAsync_WithBothFields_ReturnsTrue()
    {
        var adapter = new EmailChannelAdapter(new HttpClient(new CapturingHandler()));

        var valid = await adapter.ValidateConfigAsync(
            new Dictionary<string, string> { ["relay_url"] = "https://x", ["to"] = "a@b" },
            TestContext.Current.CancellationToken);

        valid.ShouldBeTrue();
    }

    [Fact]
    public async Task ValidateConfigAsync_WithOnlyRelayUrl_ReturnsFalse()
    {
        var adapter = new EmailChannelAdapter(new HttpClient(new CapturingHandler()));

        var valid = await adapter.ValidateConfigAsync(
            new Dictionary<string, string> { ["relay_url"] = "https://x" },
            TestContext.Current.CancellationToken);

        valid.ShouldBeFalse();
    }

    [Fact]
    public void Type_IsEmail()
    {
        new EmailChannelAdapter(new HttpClient(new CapturingHandler())).Type.ShouldBe(ChannelType.Email);
    }
}
