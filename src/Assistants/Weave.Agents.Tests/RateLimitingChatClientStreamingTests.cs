using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Weave.Agents.Pipeline;

namespace Weave.Agents.Tests;

public sealed class RateLimitingChatClientStreamingTests
{
    [Fact]
    public async Task GetStreamingResponseAsync_WithinLimit_PassesThroughUpdates()
    {
        var inner = new StreamingStubChatClient
        {
            Updates = [
                new() { Role = ChatRole.Assistant, Contents = [new TextContent("hello")] },
                new() { Role = ChatRole.Assistant, Contents = [new TextContent(" world")] }
            ]
        };
        var logger = Substitute.For<ILogger<RateLimitingChatClient>>();
        using var client = new RateLimitingChatClient(inner, maxRequestsPerMinute: 5, logger);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            null,
            TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        updates.Count.ShouldBe(2);
        updates[0].Contents.OfType<TextContent>().Single().Text.ShouldBe("hello");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_ExceedsLimit_ThrowsOperationCanceled()
    {
        var inner = new StreamingStubChatClient
        {
            Updates = [new() { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] }]
        };
        var logger = Substitute.For<ILogger<RateLimitingChatClient>>();
        using var client = new RateLimitingChatClient(inner, maxRequestsPerMinute: 1, logger);

        await Drain(client, TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Should.ThrowAsync<OperationCanceledException>(() => Drain(client, cts.Token));
    }

    private static async Task Drain(RateLimitingChatClient client, CancellationToken ct)
    {
        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            null,
            ct))
        {
        }
    }
}
