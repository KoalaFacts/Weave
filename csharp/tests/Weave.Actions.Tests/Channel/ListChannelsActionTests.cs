using System.Net;
using Weave.Actions.Channel;
using Weave.Actions.Context;
using Weave.Actions.Tests.Helpers;

namespace Weave.Actions.Tests.Channel;

public sealed class ListChannelsActionTests
{
    [Fact]
    public async Task ExecuteAsync_LiveChannels_ReturnsOpaqueElements()
    {
        const string body = """[{"channelId":"slack-1"},{"channelId":"slack-2"}]""";
        using var client = HttpClientReturning(HttpStatusCode.OK, body);
        var action = new ListChannelsAction(client);

        var result = await action.ExecuteAsync(new ListChannelsInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Channels.Count.ShouldBe(2);
        result.Value.Channels[0].GetProperty("channelId").GetString().ShouldBe("slack-1");
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_ReturnsSiloUnreachable()
    {
        using var client = new HttpClient(StubHttpMessageHandler.Throws(new HttpRequestException("nope")))
        {
            BaseAddress = new Uri("http://example.test")
        };
        var action = new ListChannelsAction(client);

        var result = await action.ExecuteAsync(new ListChannelsInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
    }

    [Fact]
    public async Task ExecuteAsync_TargetsChannelsEndpoint()
    {
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.OK, "[]");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new ListChannelsAction(client);

        await action.ExecuteAsync(new ListChannelsInput("ws-1"), CancellationToken.None);

        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/workspaces/ws-1/channels");
    }

    [Fact]
    public async Task ExecuteAsync_NullInput_Throws()
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "[]");
        var action = new ListChannelsAction(client);

        await Should.ThrowAsync<ArgumentNullException>(
            () => action.ExecuteAsync(null!, CancellationToken.None));
    }

    private static HttpClient HttpClientReturning(HttpStatusCode status, string body)
        => new(StubHttpMessageHandler.Returns(status, body)) { BaseAddress = new Uri("http://example.test") };
}
