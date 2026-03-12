using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Weave.Agents.Pipeline;

namespace Weave.Agents.Tests;

public sealed class CostTrackingChatClientTests
{
    private static (CostTrackingChatClient Client, IChatClient Inner) CreateClient()
    {
        var inner = Substitute.For<IChatClient>();
        var logger = Substitute.For<ILogger<CostTrackingChatClient>>();
        var client = new CostTrackingChatClient(inner, logger);
        return (client, inner);
    }

    private static ChatOptions WithAgentId(string agentId) =>
        new() { AdditionalProperties = new AdditionalPropertiesDictionary { ["agentId"] = agentId } };

    [Fact]
    public async Task GetResponseAsync_WithUsage_TracksCost()
    {
        var (client, inner) = CreateClient();
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Hello")])
        {
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 },
            ModelId = "claude-sonnet-4-20250514"
        };

        inner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(response);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")], WithAgentId("agent-1"), CancellationToken.None);

        var summary = client.GetCostSummary("agent-1");
        summary.ShouldNotBeNull();
        summary.TotalInputTokens.ShouldBe(100);
        summary.TotalOutputTokens.ShouldBe(50);
        summary.RequestCount.ShouldBe(1);
        summary.LastModel.ShouldBe("claude-sonnet-4-20250514");
    }

    [Fact]
    public async Task GetResponseAsync_MultipleRequests_AccumulatesCost()
    {
        var (client, inner) = CreateClient();
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "OK")])
        {
            Usage = new UsageDetails { InputTokenCount = 50, OutputTokenCount = 25 },
            ModelId = "test-model"
        };

        inner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(response);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "A")], WithAgentId("agent-1"), CancellationToken.None);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "B")], WithAgentId("agent-1"), CancellationToken.None);

        var summary = client.GetCostSummary("agent-1");
        summary.ShouldNotBeNull();
        summary.TotalInputTokens.ShouldBe(100);
        summary.TotalOutputTokens.ShouldBe(50);
        summary.RequestCount.ShouldBe(2);
    }

    [Fact]
    public void GetCostSummary_UnknownAgent_ReturnsNull()
    {
        var (client, _) = CreateClient();

        client.GetCostSummary("nonexistent").ShouldBeNull();
    }

    [Fact]
    public async Task GetAllCosts_TracksMultipleAgents()
    {
        var (client, inner) = CreateClient();
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "OK")])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            ModelId = "test"
        };

        inner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(response);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "A")], WithAgentId("agent-1"), CancellationToken.None);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "B")], WithAgentId("agent-2"), CancellationToken.None);

        var allCosts = client.GetAllCosts();
        allCosts.Count.ShouldBe(2);
        allCosts.ShouldContainKey("agent-1");
        allCosts.ShouldContainKey("agent-2");
    }

    [Fact]
    public async Task GetResponseAsync_WithNoUsage_DoesNotTrack()
    {
        var (client, inner) = CreateClient();
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "OK")]);

        inner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(response);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")], WithAgentId("agent-1"), CancellationToken.None);

        client.GetCostSummary("agent-1").ShouldBeNull();
    }

    [Fact]
    public async Task GetResponseAsync_WithNoAgentId_UsesUnknown()
    {
        var (client, inner) = CreateClient();
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "OK")])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 }
        };

        inner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(response);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")], null, CancellationToken.None);

        client.GetCostSummary("unknown").ShouldNotBeNull();
    }
}
