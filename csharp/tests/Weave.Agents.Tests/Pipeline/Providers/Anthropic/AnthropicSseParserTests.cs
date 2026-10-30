using System.Text;
using Weave.Agents.Pipeline.Providers.Anthropic;

namespace Weave.Agents.Tests.Pipeline.Providers.Anthropic;

public sealed class AnthropicSseParserTests
{
    [Fact]
    public async Task ReadEventsAsync_SingleEvent_YieldsOnePair()
    {
        const string body = "event: ping\ndata: {\"type\":\"ping\"}\n\n";

        var events = await CollectAsync(body);

        events.Count.ShouldBe(1);
        events[0].Event.ShouldBe("ping");
        events[0].Data.ShouldBe("{\"type\":\"ping\"}");
    }

    [Fact]
    public async Task ReadEventsAsync_MultipleEvents_YieldsInOrder()
    {
        const string body =
            "event: message_start\ndata: {\"a\":1}\n\n" +
            "event: content_block_delta\ndata: {\"b\":2}\n\n" +
            "event: message_stop\ndata: {}\n\n";

        var events = await CollectAsync(body);

        events.Count.ShouldBe(3);
        events.Select(e => e.Event).ShouldBe(["message_start", "content_block_delta", "message_stop"]);
    }

    [Fact]
    public async Task ReadEventsAsync_FinalEventWithoutTrailingBlankLine_StillYielded()
    {
        const string body = "event: message_stop\ndata: {}";

        var events = await CollectAsync(body);

        events.Count.ShouldBe(1);
        events[0].Event.ShouldBe("message_stop");
    }

    [Fact]
    public async Task ReadEventsAsync_CommentLines_AreIgnored()
    {
        const string body =
            ": heartbeat comment\n" +
            "event: ping\ndata: {}\n\n";

        var events = await CollectAsync(body);

        events.Count.ShouldBe(1);
        events[0].Event.ShouldBe("ping");
    }

    [Fact]
    public async Task ReadEventsAsync_MultiLineData_JoinsWithNewline()
    {
        const string body = "event: x\ndata: line1\ndata: line2\n\n";

        var events = await CollectAsync(body);

        events.Count.ShouldBe(1);
        events[0].Data.ShouldBe("line1\nline2");
    }

    [Fact]
    public async Task ReadEventsAsync_DataValueWithoutSpaceAfterColon_StillRead()
    {
        const string body = "event:ping\ndata:{}\n\n";

        var events = await CollectAsync(body);

        events.Count.ShouldBe(1);
        events[0].Event.ShouldBe("ping");
        events[0].Data.ShouldBe("{}");
    }

    [Fact]
    public async Task ReadEventsAsync_EmptyStream_YieldsNothing()
    {
        var events = await CollectAsync(string.Empty);
        events.ShouldBeEmpty();
    }

    private static async Task<List<AnthropicSseEvent>> CollectAsync(string body)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var events = new List<AnthropicSseEvent>();
        await foreach (var sse in AnthropicSseParser.ReadEventsAsync(stream, TestContext.Current.CancellationToken))
        {
            events.Add(sse);
        }
        return events;
    }
}
