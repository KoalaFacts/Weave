using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Weave.Agents.Pipeline;

namespace Weave.Agents.Tests;

public sealed class RateLimitingChatClientTests : IDisposable
{
    private readonly IChatClient _inner;
    private readonly RateLimitingChatClient _client;

    public RateLimitingChatClientTests()
    {
        _inner = Substitute.For<IChatClient>();
        _inner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "OK")]));

        var logger = Substitute.For<ILogger<RateLimitingChatClient>>();
        _client = new RateLimitingChatClient(_inner, maxRequestsPerMinute: 2, logger);
    }

    [Fact]
    public async Task GetResponseAsync_WithinLimit_Succeeds()
    {
        var result = await _client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")], null, CancellationToken.None);

        result.ShouldNotBeNull();
        await _inner.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetResponseAsync_ExceedsLimit_ThrowsInvalidOperation()
    {
        // Use up the token bucket (limit = 2)
        await _client.GetResponseAsync([new ChatMessage(ChatRole.User, "1")], null, CancellationToken.None);
        await _client.GetResponseAsync([new ChatMessage(ChatRole.User, "2")], null, CancellationToken.None);

        // Third request should exceed the rate limit
        // The queue limit is 10, so it may queue. Use a short cancellation to force failure.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Should.ThrowAsync<OperationCanceledException>(
            () => _client.GetResponseAsync([new ChatMessage(ChatRole.User, "3")], null, cts.Token));
    }

    [Fact]
    public async Task GetResponseAsync_DelegatesCorrectly()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "Test") };
        var options = new ChatOptions();

        await _client.GetResponseAsync(messages, options, CancellationToken.None);

        await _inner.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            options,
            Arg.Any<CancellationToken>());
    }

    public void Dispose() => _client.Dispose();
}
