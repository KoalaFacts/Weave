using Microsoft.Extensions.AI;
using Weave.Agents.Pipeline.Providers.Anthropic;

namespace Weave.Agents.Tests.Pipeline.Providers.Anthropic;

public sealed class AnthropicChatClientStreamingTests
{
    private const string SampleStream =
        "event: message_start\n" +
        "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_streaming\",\"model\":\"claude-streaming-model\",\"usage\":{\"input_tokens\":12,\"output_tokens\":0}}}\n\n" +
        "event: content_block_start\n" +
        "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
        "event: ping\n" +
        "data: {\"type\":\"ping\"}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello, \"}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"world!\"}}\n\n" +
        "event: content_block_stop\n" +
        "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "event: message_delta\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":34}}\n\n" +
        "event: message_stop\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    [Fact]
    public async Task GetStreamingResponseAsync_RealSse_YieldsTextDeltasInOrder()
    {
        var (client, _) = CreateClient(SampleStream);

        var updates = await CollectAsync(client);

        var textPieces = updates.SelectMany(u => u.Contents.OfType<TextContent>().Select(c => c.Text)).ToList();
        textPieces.ShouldBe(["Hello, ", "world!"]);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_RealSse_FinalUpdateHasUsageAndFinishReason()
    {
        var (client, _) = CreateClient(SampleStream);

        var updates = await CollectAsync(client);

        var final = updates[^1];
        final.FinishReason.ShouldBe(ChatFinishReason.Stop);
        var usage = final.Contents.OfType<UsageContent>().Single().Details;
        usage.InputTokenCount.ShouldBe(12);
        usage.OutputTokenCount.ShouldBe(34);
        final.ResponseId.ShouldBe("msg_streaming");
        final.ModelId.ShouldBe("claude-streaming-model");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_RequestBody_HasStreamTrue()
    {
        var (client, handler) = CreateClient(SampleStream);

        await CollectAsync(client);

        handler.LastRequestBody.ShouldNotBeNull();
        handler.LastRequestBody.ShouldContain("\"stream\":true");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_NullMessages_Throws()
    {
        var (client, _) = CreateClient(SampleStream);

        await Should.ThrowAsync<ArgumentNullException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(null!, cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        });
    }

    [Fact]
    public async Task GetStreamingResponseAsync_OptionsModelId_OverridesDefault_InRequestBody()
    {
        var (client, handler) = CreateClient(SampleStream);

        await CollectAsync(
            client,
            new ChatOptions { ModelId = "claude-stream-override" });

        handler.LastRequestBody!.ShouldContain("\"model\":\"claude-stream-override\"");
    }

    private static (AnthropicChatClient client, StubHttpMessageHandler handler) CreateClient(string body)
    {
        var handler = new StubHttpMessageHandler().Returns(System.Net.HttpStatusCode.OK, body);
        var http = new HttpClient(handler);
        var client = new AnthropicChatClient(http, "claude-sonnet-4-20250514", "sk-ant-test");
        return (client, handler);
    }

    private static async Task<List<ChatResponseUpdate>> CollectAsync(AnthropicChatClient client, ChatOptions? options = null)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            options,
            TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }
        return updates;
    }
}
