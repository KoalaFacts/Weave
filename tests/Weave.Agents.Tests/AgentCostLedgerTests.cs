using Weave.Agents.Pipeline;

namespace Weave.Agents.Tests;

public sealed class AgentCostLedgerTests
{
    private readonly AgentCostLedger _ledger = new();

    [Fact]
    public void RecordUsage_SingleEntry_StoresCorrectly()
    {
        _ledger.RecordUsage("agent-1", "gpt-4", 100, 50);

        var summary = _ledger.GetCostSummary("agent-1");
        summary.ShouldNotBeNull();
        summary!.TotalInputTokens.ShouldBe(100);
        summary.TotalOutputTokens.ShouldBe(50);
        summary.RequestCount.ShouldBe(1);
        summary.LastModel.ShouldBe("gpt-4");
    }

    [Fact]
    public void RecordUsage_MultipleEntries_Accumulates()
    {
        _ledger.RecordUsage("agent-1", "gpt-4", 100, 50);
        _ledger.RecordUsage("agent-1", "gpt-4o", 200, 75);

        var summary = _ledger.GetCostSummary("agent-1");
        summary.ShouldNotBeNull();
        summary!.TotalInputTokens.ShouldBe(300);
        summary.TotalOutputTokens.ShouldBe(125);
        summary.RequestCount.ShouldBe(2);
        summary.LastModel.ShouldBe("gpt-4o");
    }

    [Fact]
    public void GetCostSummary_UnknownAgent_ReturnsNull()
    {
        _ledger.GetCostSummary("nonexistent").ShouldBeNull();
    }

    [Fact]
    public void GetAllCosts_Empty_ReturnsEmptyDictionary()
    {
        _ledger.GetAllCosts().ShouldBeEmpty();
    }

    [Fact]
    public void GetAllCosts_MultipleAgents_ReturnsAll()
    {
        _ledger.RecordUsage("agent-1", "gpt-4", 100, 50);
        _ledger.RecordUsage("agent-2", "claude", 200, 75);

        var all = _ledger.GetAllCosts();
        all.Count.ShouldBe(2);
        all.ShouldContainKey("agent-1");
        all.ShouldContainKey("agent-2");
    }

    [Fact]
    public void RecordUsage_ZeroTokens_StillIncrementsRequestCount()
    {
        _ledger.RecordUsage("agent-1", "gpt-4", 0, 0);

        var summary = _ledger.GetCostSummary("agent-1");
        summary.ShouldNotBeNull();
        summary!.RequestCount.ShouldBe(1);
        summary.TotalInputTokens.ShouldBe(0);
    }
}
