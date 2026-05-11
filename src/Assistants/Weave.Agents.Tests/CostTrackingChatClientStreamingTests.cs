using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Weave.Agents.Pipeline;

namespace Weave.Agents.Tests;

public sealed class CostTrackingChatClientStreamingTests
{
    private static (CostTrackingChatClient Client, StreamingStubChatClient Inner) CreateClient()
    {
        var inner = new StreamingStubChatClient();
        var logger = Substitute.For<ILogger<CostTrackingChatClient>>();
        var client = new CostTrackingChatClient(inner, logger);
        return (client, inner);
    }

    private static ChatOptions WithAgentId(string agentId) =>
        new() { AdditionalProperties = new AdditionalPropertiesDictionary { ["agentId"] = agentId } };

    [Fact]
    public async Task GetStreamingResponseAsync_UsageContentInFinalUpdate_RecordsCostOnce()
    {
        var (client, inner) = CreateClient();
        inner.Updates = [
            new() { Role = ChatRole.Assistant, Contents = [new TextContent("a")], ModelId = "claude-x" },
            new() { Role = ChatRole.Assistant, Contents = [new TextContent("b")], ModelId = "claude-x" },
            new()
            {
                Role = ChatRole.Assistant,
                Contents = [new UsageContent(new UsageDetails { InputTokenCount = 30, OutputTokenCount = 12 })],
                ModelId = "claude-x"
            }
        ];

        await Collect(client, WithAgentId("agent-1"));

        var summary = client.GetCostSummary("agent-1");
        summary.ShouldNotBeNull();
        summary.TotalInputTokens.ShouldBe(30);
        summary.TotalOutputTokens.ShouldBe(12);
        summary.RequestCount.ShouldBe(1);
        summary.LastModel.ShouldBe("claude-x");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_NoUsageContent_DoesNotRecord()
    {
        var (client, inner) = CreateClient();
        inner.Updates = [
            new() { Role = ChatRole.Assistant, Contents = [new TextContent("only text")], ModelId = "claude-x" }
        ];

        await Collect(client, WithAgentId("agent-1"));

        client.GetCostSummary("agent-1").ShouldBeNull();
    }

    [Fact]
    public async Task GetStreamingResponseAsync_PassesThroughAllUpdates()
    {
        var (client, inner) = CreateClient();
        inner.Updates = [
            new() { Role = ChatRole.Assistant, Contents = [new TextContent("one")] },
            new() { Role = ChatRole.Assistant, Contents = [new TextContent("two")] },
            new() { Role = ChatRole.Assistant, Contents = [new TextContent("three")] }
        ];

        var received = await Collect(client, WithAgentId("agent-1"));

        received.Select(u => u.Contents.OfType<TextContent>().Single().Text).ShouldBe(["one", "two", "three"]);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_LaterUsageOverwritesEarlier_LastWins()
    {
        var (client, inner) = CreateClient();
        inner.Updates = [
            new() { Contents = [new UsageContent(new UsageDetails { InputTokenCount = 1, OutputTokenCount = 1 })], ModelId = "m" },
            new() { Contents = [new UsageContent(new UsageDetails { InputTokenCount = 99, OutputTokenCount = 88 })], ModelId = "m" }
        ];

        await Collect(client, WithAgentId("agent-1"));

        var summary = client.GetCostSummary("agent-1")!;
        summary.TotalInputTokens.ShouldBe(99);
        summary.TotalOutputTokens.ShouldBe(88);
    }

    private static async Task<List<ChatResponseUpdate>> Collect(CostTrackingChatClient client, ChatOptions options)
    {
        var list = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, TestContext.Current.CancellationToken))
        {
            list.Add(update);
        }
        return list;
    }
}
