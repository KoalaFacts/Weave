using System.Net;
using System.Text.Json;
using Weave.Actions.Channel;
using Weave.Actions.Context;
using Weave.Actions.Tests.Helpers;

namespace Weave.Actions.Tests.Channel;

public sealed class PostChannelActionTests
{
    private static readonly JsonElement SampleChannel =
        JsonSerializer.Deserialize<JsonElement>("""{"channelId":"slack-1"}""");

    [Fact]
    public async Task ExecuteAsync_SuccessStatus_ReturnsSuccess()
    {
        using var client = HttpClientReturning(HttpStatusCode.NoContent);
        var action = new PostChannelAction(client);

        var result = await action.ExecuteAsync(new PostChannelInput("ws-1", SampleChannel), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_BadRequest_ReturnsValidationFailed()
    {
        using var client = HttpClientReturning(HttpStatusCode.BadRequest);
        var action = new PostChannelAction(client);

        var result = await action.ExecuteAsync(new PostChannelInput("ws-1", SampleChannel), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.ValidationFailed);
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_ReturnsSiloUnreachable()
    {
        using var client = new HttpClient(StubHttpMessageHandler.Throws(new HttpRequestException("nope")))
        {
            BaseAddress = new Uri("http://example.test")
        };
        var action = new PostChannelAction(client);

        var result = await action.ExecuteAsync(new PostChannelInput("ws-1", SampleChannel), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
    }

    [Fact]
    public async Task ExecuteAsync_TargetsChannelsEndpointWithPostMethod()
    {
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.NoContent);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new PostChannelAction(client);

        await action.ExecuteAsync(new PostChannelInput("ws-1", SampleChannel), CancellationToken.None);

        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/workspaces/ws-1/channels");
        handler.LastRequestMethod.ShouldBe(HttpMethod.Post);
    }

    private static HttpClient HttpClientReturning(HttpStatusCode status)
        => new(StubHttpMessageHandler.Returns(status)) { BaseAddress = new Uri("http://example.test") };
}
