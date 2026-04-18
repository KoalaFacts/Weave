using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

public sealed class SkillMemoryGrainTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");

    private static IPersistentState<SkillMemoryState> CreatePersistentState()
    {
        var state = new SkillMemoryState
        {
            WorkspaceId = TestWorkspaceId.ToString()
        };

        var persistentState = Substitute.For<IPersistentState<SkillMemoryState>>();
        persistentState.State.Returns(state);
        persistentState.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync().Returns(Task.CompletedTask);
        persistentState.ClearStateAsync().Returns(Task.CompletedTask);
        return persistentState;
    }

    private static (SkillMemoryGrain Grain, IEventBus EventBus) CreateGrain()
    {
        var eventBus = Substitute.For<IEventBus>();
        var logger = NullLogger<SkillMemoryGrain>.Instance;
        var persistentState = CreatePersistentState();

        var grain = new SkillMemoryGrain(eventBus, logger, persistentState);
        return (grain, eventBus);
    }

    private static SkillDocument CreateSkill(
        string? id = null,
        string title = "Deploy to Kubernetes",
        string description = "Steps to deploy a containerized application to a Kubernetes cluster",
        List<string>? tags = null,
        string createdByAgent = "researcher",
        int useCount = 0,
        double successRate = 1.0)
    {
        return new SkillDocument
        {
            SkillId = SkillId.From(id ?? Guid.NewGuid().ToString("N")),
            Title = title,
            Description = description,
            Tags = tags ?? ["deploy", "kubernetes", "containers"],
            Steps =
            [
                new SkillStep { Order = 1, Action = "Build Docker image", ToolName = "shell", ExpectedOutcome = "Image built" },
                new SkillStep { Order = 2, Action = "Push to registry", ToolName = "shell", ExpectedOutcome = "Image pushed" }
            ],
            ToolsUsed = ["shell", "code-search"],
            CreatedByAgent = createdByAgent,
            UseCount = useCount,
            SuccessRate = successRate
        };
    }

    [Fact]
    public async Task StoreSkillAsync_PersistsSkill()
    {
        var (grain, _) = CreateGrain();
        var skill = CreateSkill(id: "skill-1");

        var result = await grain.StoreSkillAsync(skill);

        result.ShouldNotBeNull();
        result.SkillId.ShouldBe(skill.SkillId);
        result.Title.ShouldBe("Deploy to Kubernetes");

        var retrieved = await grain.GetSkillAsync(skill.SkillId);
        retrieved.ShouldNotBeNull();
        retrieved.SkillId.ShouldBe(skill.SkillId);
    }

    [Fact]
    public async Task SearchAsync_MatchesByTags()
    {
        var (grain, _) = CreateGrain();
        var skill = CreateSkill(tags: ["deploy", "kubernetes", "containers"]);
        await grain.StoreSkillAsync(skill);

        var results = await grain.SearchAsync("kubernetes");

        results.ShouldNotBeEmpty();
        results[0].Skill.SkillId.ShouldBe(skill.SkillId);
        results[0].RelevanceScore.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task SearchAsync_MatchesByTitleKeywords()
    {
        var (grain, _) = CreateGrain();
        var skill = CreateSkill(title: "Automated database migration");
        await grain.StoreSkillAsync(skill);

        var results = await grain.SearchAsync("database migration");

        results.ShouldNotBeEmpty();
        results[0].Skill.SkillId.ShouldBe(skill.SkillId);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmpty_WhenNoMatch()
    {
        var (grain, _) = CreateGrain();
        var skill = CreateSkill(
            title: "Deploy to Kubernetes",
            description: "Kubernetes deployment steps",
            tags: ["deploy", "kubernetes"]);
        await grain.StoreSkillAsync(skill);

        var results = await grain.SearchAsync("quantum entanglement");

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_RanksHigherUseCountFirst()
    {
        var (grain, _) = CreateGrain();

        var lowUse = CreateSkill(
            id: "low-use",
            title: "Deploy app",
            tags: ["deploy"],
            useCount: 1);
        var highUse = CreateSkill(
            id: "high-use",
            title: "Deploy service",
            tags: ["deploy"],
            useCount: 50);

        await grain.StoreSkillAsync(lowUse);
        await grain.StoreSkillAsync(highUse);

        var results = await grain.SearchAsync("deploy");

        results.Count.ShouldBeGreaterThanOrEqualTo(2);
        results[0].Skill.SkillId.ShouldBe(highUse.SkillId);
        results[0].RelevanceScore.ShouldBeGreaterThan(results[1].RelevanceScore);
    }

    [Fact]
    public async Task RecordUsageAsync_IncrementsCount()
    {
        var (grain, _) = CreateGrain();
        var skill = CreateSkill(id: "skill-usage");
        await grain.StoreSkillAsync(skill);

        await grain.RecordUsageAsync(skill.SkillId, success: true);

        var updated = await grain.GetSkillAsync(skill.SkillId);
        updated.ShouldNotBeNull();
        updated.UseCount.ShouldBe(1);
        updated.LastUsedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task RecordUsageAsync_UpdatesSuccessRate()
    {
        var (grain, _) = CreateGrain();
        var skill = CreateSkill(id: "skill-rate", useCount: 0, successRate: 1.0);
        await grain.StoreSkillAsync(skill);

        // First usage: success -> rate = (1.0 * 0 + 1.0) / 1 = 1.0
        await grain.RecordUsageAsync(skill.SkillId, success: true);
        var afterFirst = await grain.GetSkillAsync(skill.SkillId);
        afterFirst.ShouldNotBeNull();
        afterFirst.SuccessRate.ShouldBe(1.0);

        // Second usage: failure -> rate = (1.0 * 1 + 0.0) / 2 = 0.5
        await grain.RecordUsageAsync(skill.SkillId, success: false);
        var afterSecond = await grain.GetSkillAsync(skill.SkillId);
        afterSecond.ShouldNotBeNull();
        afterSecond.SuccessRate.ShouldBe(0.5, 0.001);
    }

    [Fact]
    public async Task RemoveSkillAsync_DeletesSkill()
    {
        var (grain, _) = CreateGrain();
        var skill = CreateSkill(id: "skill-remove");
        await grain.StoreSkillAsync(skill);

        await grain.RemoveSkillAsync(skill.SkillId);

        var result = await grain.GetSkillAsync(skill.SkillId);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetSkillAsync_ReturnsNull_WhenNotFound()
    {
        var (grain, _) = CreateGrain();

        var result = await grain.GetSkillAsync(SkillId.From("nonexistent"));

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetAllSkillsAsync_ReturnsAllSkills()
    {
        var (grain, _) = CreateGrain();
        var skill1 = CreateSkill(id: "skill-a", title: "First Skill");
        var skill2 = CreateSkill(id: "skill-b", title: "Second Skill");
        await grain.StoreSkillAsync(skill1);
        await grain.StoreSkillAsync(skill2);

        var all = await grain.GetAllSkillsAsync();

        all.Count.ShouldBe(2);
        all.ShouldContain(s => s.SkillId == skill1.SkillId);
        all.ShouldContain(s => s.SkillId == skill2.SkillId);
    }
}
