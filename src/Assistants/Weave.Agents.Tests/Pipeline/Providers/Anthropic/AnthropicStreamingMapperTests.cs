using Microsoft.Extensions.AI;
using Weave.Agents.Pipeline.Providers.Anthropic;

namespace Weave.Agents.Tests.Pipeline.Providers.Anthropic;

public sealed class AnthropicStreamingMapperTests
{
    [Fact]
    public async Task MapEventsAsync_FullSequence_YieldsDeltasThenFinalUpdate()
    {
        var events = ToAsync(
            ("message_start", "{\"message\":{\"id\":\"msg_1\",\"model\":\"claude-x\",\"usage\":{\"input_tokens\":7,\"output_tokens\":0}}}"),
            ("content_block_delta", "{\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello \"}}"),
            ("content_block_delta", "{\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"world\"}}"),
            ("message_delta", "{\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"input_tokens\":0,\"output_tokens\":4}}"),
            ("message_stop", "{}"));

        var updates = await CollectAsync(events, "claude-default");

        updates.Count.ShouldBe(3);
        updates[0].Contents.OfType<TextContent>().Single().Text.ShouldBe("Hello ");
        updates[0].ModelId.ShouldBe("claude-x");
        updates[0].ResponseId.ShouldBe("msg_1");
        updates[1].Contents.OfType<TextContent>().Single().Text.ShouldBe("world");

        var final = updates[2];
        final.FinishReason.ShouldBe(ChatFinishReason.Stop);
        final.ResponseId.ShouldBe("msg_1");
        final.ModelId.ShouldBe("claude-x");
        var usage = final.Contents.OfType<UsageContent>().Single().Details;
        usage.InputTokenCount.ShouldBe(7);
        usage.OutputTokenCount.ShouldBe(4);
    }

    [Fact]
    public async Task MapEventsAsync_EmptyTextDelta_NotYielded()
    {
        var events = ToAsync(
            ("content_block_delta", "{\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"\"}}"),
            ("message_stop", "{}"));

        var updates = await CollectAsync(events, "m");

        updates.Count.ShouldBe(1);
        updates[0].Contents.OfType<TextContent>().ShouldBeEmpty();
    }

    [Fact]
    public async Task MapEventsAsync_NonTextDelta_NotYielded()
    {
        var events = ToAsync(
            ("content_block_delta", "{\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"text\":\"ignored\"}}"),
            ("message_stop", "{}"));

        var updates = await CollectAsync(events, "m");

        updates.Count.ShouldBe(1);
        updates[0].Contents.OfType<TextContent>().ShouldBeEmpty();
    }

    [Fact]
    public async Task MapEventsAsync_NoUsage_FinalUpdateHasNoUsageContent()
    {
        var events = ToAsync(
            ("message_start", "{\"message\":{\"id\":\"m1\",\"model\":\"x\"}}"),
            ("message_stop", "{}"));

        var updates = await CollectAsync(events, "default");

        updates.Count.ShouldBe(1);
        updates[0].Contents.OfType<UsageContent>().ShouldBeEmpty();
        updates[0].ResponseId.ShouldBe("m1");
    }

    [Fact]
    public async Task MapEventsAsync_ModelIdMissingFromStart_FallsBackToDefault()
    {
        var events = ToAsync(
            ("content_block_delta", "{\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}"),
            ("message_stop", "{}"));

        var updates = await CollectAsync(events, "default-model");

        updates[0].ModelId.ShouldBe("default-model");
        updates[1].ModelId.ShouldBe("default-model");
    }

    [Fact]
    public async Task MapEventsAsync_UnknownEvent_IsIgnored()
    {
        var events = ToAsync(
            ("ping", "{}"),
            ("unknown_future_event", "{}"),
            ("message_stop", "{}"));

        var updates = await CollectAsync(events, "m");

        updates.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("end_turn", "stop")]
    [InlineData("max_tokens", "length")]
    [InlineData("stop_sequence", "stop")]
    [InlineData("tool_use", "tool_calls")]
    public void MapFinishReason_MapsKnownStopReasons(string stopReason, string expectedName)
    {
        AnthropicStreamingMapper.MapFinishReason(stopReason)!.Value.ToString().ShouldBe(expectedName);
    }

    [Fact]
    public void MapFinishReason_UnknownStopReason_ReturnsNull()
    {
        AnthropicStreamingMapper.MapFinishReason("something_new").ShouldBeNull();
        AnthropicStreamingMapper.MapFinishReason(null).ShouldBeNull();
    }

    [Fact]
    public async Task MapEventsAsync_ErrorEvent_ThrowsInvalidOperationException()
    {
        var events = ToAsync(
            ("message_start", "{\"message\":{\"id\":\"m1\",\"model\":\"x\"}}"),
            ("error", "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"<<MARKER-ERROR>>\"}}"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in AnthropicStreamingMapper.MapEventsAsync(events, "m", TestContext.Current.CancellationToken))
            {
            }
        });
        ex.Message.ShouldContain("<<MARKER-ERROR>>");
    }

    private static async IAsyncEnumerable<AnthropicSseEvent> ToAsync(params (string Event, string Data)[] events)
    {
        foreach (var (e, d) in events)
        {
            yield return new AnthropicSseEvent(e, d);
            await Task.Yield();
        }
    }

    private static async Task<List<ChatResponseUpdate>> CollectAsync(IAsyncEnumerable<AnthropicSseEvent> events, string defaultModelId)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in AnthropicStreamingMapper.MapEventsAsync(events, defaultModelId, TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }
        return updates;
    }
}
