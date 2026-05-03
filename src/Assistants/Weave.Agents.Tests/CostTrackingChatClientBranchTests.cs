using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Weave.Agents.Pipeline;

namespace Weave.Agents.Tests;

/// <summary>
/// Branch coverage for <see cref="CostTrackingChatClient"/> — fills
/// the paths the happy-path tests in <see cref="CostTrackingChatClientTests"/>
/// don't cover: the ledger-injected constructor, the null-usage path,
/// and the "unknown agent" fallback when options don't name one.
/// </summary>
public sealed class CostTrackingChatClientBranchTests
{
    public sealed class InjectedLedgerConstructor
    {
        [Fact]
        public async Task Uses_injected_ledger_to_record_usage()
        {
            var inner = Substitute.For<IChatClient>();
            var ledger = Substitute.For<IAgentCostLedger>();
            var logger = Substitute.For<ILogger<CostTrackingChatClient>>();
            using var client = new CostTrackingChatClient(inner, ledger, logger);

            var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")])
            {
                Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
                ModelId = "test-model"
            };
            inner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
                .Returns(response);

            await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")],
                new ChatOptions { AdditionalProperties = new() { ["agentId"] = "agent-1" } },
                CancellationToken.None);

            ledger.Received(1).RecordUsage("agent-1", "test-model", 10, 5);
        }

        [Fact]
        public void GetAllCosts_delegates_to_ledger()
        {
            var inner = Substitute.For<IChatClient>();
            var ledger = Substitute.For<IAgentCostLedger>();
            var expected = new Dictionary<string, AgentCostSummary>
            {
                ["agent-a"] = new AgentCostSummary { TotalInputTokens = 100 }
            };
            ledger.GetAllCosts().Returns(expected);

            using var client = new CostTrackingChatClient(inner, ledger, Substitute.For<ILogger<CostTrackingChatClient>>());

            client.GetAllCosts().ShouldBe(expected);
        }

        [Fact]
        public void GetCostSummary_delegates_to_ledger()
        {
            var inner = Substitute.For<IChatClient>();
            var ledger = Substitute.For<IAgentCostLedger>();
            var expected = new AgentCostSummary { TotalInputTokens = 42 };
            ledger.GetCostSummary("agent-x").Returns(expected);

            using var client = new CostTrackingChatClient(inner, ledger, Substitute.For<ILogger<CostTrackingChatClient>>());

            client.GetCostSummary("agent-x").ShouldBe(expected);
        }
    }

    public sealed class UsageHandling
    {
        [Fact]
        public async Task Response_without_usage_does_not_record()
        {
            // Happy path asserts the record call happens; this branch
            // proves that a ChatResponse with null Usage doesn't call
            // the ledger at all.
            var inner = Substitute.For<IChatClient>();
            var ledger = Substitute.For<IAgentCostLedger>();
            var logger = Substitute.For<ILogger<CostTrackingChatClient>>();
            using var client = new CostTrackingChatClient(inner, ledger, logger);

            inner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
                .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]) { Usage = null });

            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], null, CancellationToken.None);

            ledger.DidNotReceive().RecordUsage(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>());
        }

        [Fact]
        public async Task Missing_agentId_records_under_unknown()
        {
            var inner = Substitute.For<IChatClient>();
            var ledger = Substitute.For<IAgentCostLedger>();
            var logger = Substitute.For<ILogger<CostTrackingChatClient>>();
            using var client = new CostTrackingChatClient(inner, ledger, logger);

            inner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
                .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")])
                {
                    Usage = new UsageDetails { InputTokenCount = 1, OutputTokenCount = 1 },
                    ModelId = "m"
                });

            // ChatOptions is null entirely -> AdditionalProperties null -> "unknown".
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], null, CancellationToken.None);

            ledger.Received(1).RecordUsage("unknown", "m", 1, 1);
        }

        [Fact]
        public async Task Missing_modelId_records_as_unknown_model()
        {
            var inner = Substitute.For<IChatClient>();
            var ledger = Substitute.For<IAgentCostLedger>();
            var logger = Substitute.For<ILogger<CostTrackingChatClient>>();
            using var client = new CostTrackingChatClient(inner, ledger, logger);

            inner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
                .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")])
                {
                    Usage = new UsageDetails { InputTokenCount = 2, OutputTokenCount = 3 }
                    // ModelId not set
                });

            await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")],
                new ChatOptions { AdditionalProperties = new() { ["agentId"] = "a" } },
                CancellationToken.None);

            ledger.Received(1).RecordUsage("a", "unknown", 2, 3);
        }
    }
}
