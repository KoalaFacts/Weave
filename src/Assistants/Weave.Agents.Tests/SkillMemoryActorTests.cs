using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

public sealed class SkillMemoryActorTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");

    private static IActorState<SkillMemoryState> CreatePersistentState()
    {
        var state = new SkillMemoryState
        {
            WorkspaceId = TestWorkspaceId.ToString()
        };

        var persistentState = Substitute.For<IActorState<SkillMemoryState>>();
        persistentState.State.Returns(state);
        persistentState.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.ClearStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return persistentState;
    }

    private static (SkillMemoryActor Actor, IEventBus EventBus) CreateActor(TimeProvider? timeProvider = null)
    {
        var eventBus = Substitute.For<IEventBus>();
        var logger = NullLogger<SkillMemoryActor>.Instance;
        var persistentState = CreatePersistentState();

        var actor = new SkillMemoryActor(eventBus, timeProvider ?? TimeProvider.System, logger, persistentState);
        return (actor, eventBus);
    }

    private static SkillDocument CreateSkill(
        string? id = null,
        string title = "Deploy to Kubernetes",
        string description = "Steps to deploy a containerized application to a Kubernetes cluster",
        List<string>? tags = null,
        string createdByAgent = "researcher",
        int useCount = 0,
        double successRate = 1.0,
        DateTimeOffset? lastUsedAt = null)
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
            SuccessRate = successRate,
            LastUsedAt = lastUsedAt
        };
    }

    [Fact]
    public async Task StoreSkillAsync_PersistsSkill()
    {
        var (actor, _) = CreateActor();
        var skill = CreateSkill(id: "skill-1");

        var result = await actor.StoreSkillAsync(skill);

        result.ShouldNotBeNull();
        result.SkillId.ShouldBe(skill.SkillId);
        result.Title.ShouldBe("Deploy to Kubernetes");

        var retrieved = await actor.GetSkillAsync(skill.SkillId);
        retrieved.ShouldNotBeNull();
        retrieved.SkillId.ShouldBe(skill.SkillId);
    }

    [Fact]
    public async Task SuggestSkillAsync_AddsPendingSuggestionWithoutSearchableSkill()
    {
        var (actor, _) = CreateActor();
        var skill = CreateSkill(id: "suggested-skill", title: "Suggested deployment skill");

        var suggestion = await actor.SuggestSkillAsync(skill, "task-1");

        suggestion.Skill.SkillId.ShouldBe(skill.SkillId);
        suggestion.SourceTaskId.ShouldBe("task-1");
        var pending = await actor.GetSuggestedSkillsAsync();
        pending.Count.ShouldBe(1);
        var stored = await actor.GetSkillAsync(skill.SkillId);
        stored.ShouldBeNull();
    }

    [Fact]
    public async Task AcceptSuggestedSkillAsync_MovesSuggestionIntoSkills()
    {
        var (actor, _) = CreateActor();
        var skill = CreateSkill(id: "accepted-suggestion", title: "Accepted skill");
        await actor.SuggestSkillAsync(skill, "task-2");

        var accepted = await actor.AcceptSuggestedSkillAsync(skill.SkillId);

        accepted.ShouldNotBeNull();
        accepted.SkillId.ShouldBe(skill.SkillId);
        var pending = await actor.GetSuggestedSkillsAsync();
        pending.ShouldBeEmpty();
        var stored = await actor.GetSkillAsync(skill.SkillId);
        stored.ShouldNotBeNull();
    }

    [Fact]
    public async Task RejectSuggestedSkillAsync_RemovesSuggestionWithoutStoringSkill()
    {
        var (actor, _) = CreateActor();
        var skill = CreateSkill(id: "rejected-suggestion", title: "Rejected skill");
        await actor.SuggestSkillAsync(skill, "task-3");

        var rejected = await actor.RejectSuggestedSkillAsync(skill.SkillId);

        rejected.ShouldBeTrue();
        var pending = await actor.GetSuggestedSkillsAsync();
        pending.ShouldBeEmpty();
        var stored = await actor.GetSkillAsync(skill.SkillId);
        stored.ShouldBeNull();
    }

    [Fact]
    public async Task AcceptSuggestedSkillAsync_ReturnsNull_WhenSuggestionIsMissing()
    {
        var (actor, _) = CreateActor();

        var accepted = await actor.AcceptSuggestedSkillAsync(SkillId.From("missing-suggestion"));

        accepted.ShouldBeNull();
    }

    [Fact]
    public async Task RejectSuggestedSkillAsync_ReturnsFalse_WhenSuggestionIsMissing()
    {
        var (actor, _) = CreateActor();

        var rejected = await actor.RejectSuggestedSkillAsync(SkillId.From("missing-suggestion"));

        rejected.ShouldBeFalse();
    }

    [Fact]
    public async Task ArchiveSkillAsync_MarksSkillArchivedAndExcludesFromSearchAndList()
    {
        var (actor, _) = CreateActor();
        var skill = CreateSkill(id: "archived-skill", title: "Archive deployment skill", tags: ["archive"]);
        await actor.StoreSkillAsync(skill);

        var archived = await actor.ArchiveSkillAsync(skill.SkillId);

        archived.ShouldNotBeNull();
        archived.ArchivedAt.ShouldNotBeNull();
        var searchResults = await actor.SearchAsync("archive");
        searchResults.ShouldBeEmpty();
        var allSkills = await actor.GetAllSkillsAsync();
        allSkills.ShouldBeEmpty();
        var direct = await actor.GetSkillAsync(skill.SkillId);
        direct.ShouldNotBeNull();
        direct.ArchivedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task ArchiveSkillAsync_ReturnsNull_WhenSkillIsMissing()
    {
        var (actor, _) = CreateActor();

        var archived = await actor.ArchiveSkillAsync(SkillId.From("missing-archive"));

        archived.ShouldBeNull();
    }

    [Fact]
    public async Task RestoreSkillAsync_ClearsArchiveAndIncludesInSearchAndList()
    {
        var (actor, _) = CreateActor();
        var skill = CreateSkill(id: "restored-skill", title: "Restore deployment skill", tags: ["restore"]);
        await actor.StoreSkillAsync(skill);
        await actor.ArchiveSkillAsync(skill.SkillId);

        var restored = await actor.RestoreSkillAsync(skill.SkillId);

        restored.ShouldNotBeNull();
        restored.ArchivedAt.ShouldBeNull();
        var searchResults = await actor.SearchAsync("restore");
        searchResults.Count.ShouldBe(1);
        searchResults[0].Skill.SkillId.ShouldBe(skill.SkillId);
        var allSkills = await actor.GetAllSkillsAsync();
        allSkills.Count.ShouldBe(1);
        allSkills[0].SkillId.ShouldBe(skill.SkillId);
    }

    [Fact]
    public async Task RestoreSkillAsync_ReturnsNull_WhenSkillIsMissing()
    {
        var (actor, _) = CreateActor();

        var restored = await actor.RestoreSkillAsync(SkillId.From("missing-restore"));

        restored.ShouldBeNull();
    }

    [Fact]
    public async Task SearchAsync_MatchesByTags()
    {
        var (actor, _) = CreateActor();
        var skill = CreateSkill(tags: ["deploy", "kubernetes", "containers"]);
        await actor.StoreSkillAsync(skill);

        var results = await actor.SearchAsync("kubernetes");

        results.ShouldNotBeEmpty();
        results[0].Skill.SkillId.ShouldBe(skill.SkillId);
        results[0].RelevanceScore.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task SearchAsync_MatchesByTitleKeywords()
    {
        var (actor, _) = CreateActor();
        var skill = CreateSkill(title: "Automated database migration");
        await actor.StoreSkillAsync(skill);

        var results = await actor.SearchAsync("database migration");

        results.ShouldNotBeEmpty();
        results[0].Skill.SkillId.ShouldBe(skill.SkillId);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmpty_WhenNoMatch()
    {
        var (actor, _) = CreateActor();
        var skill = CreateSkill(
            title: "Deploy to Kubernetes",
            description: "Kubernetes deployment steps",
            tags: ["deploy", "kubernetes"]);
        await actor.StoreSkillAsync(skill);

        var results = await actor.SearchAsync("quantum entanglement");

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_RanksHigherUseCountFirst()
    {
        var (actor, _) = CreateActor();

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

        await actor.StoreSkillAsync(lowUse);
        await actor.StoreSkillAsync(highUse);

        var results = await actor.SearchAsync("deploy");

        results.Count.ShouldBeGreaterThanOrEqualTo(2);
        results[0].Skill.SkillId.ShouldBe(highUse.SkillId);
        results[0].RelevanceScore.ShouldBeGreaterThan(results[1].RelevanceScore);
    }

    [Fact]
    public async Task SearchAsync_FiltersByMinimumSuccessRate()
    {
        var (actor, _) = CreateActor();
        var reliable = CreateSkill(id: "reliable", title: "Deploy app", tags: ["deploy"], successRate: 0.95);
        var unreliable = CreateSkill(id: "unreliable", title: "Deploy app", tags: ["deploy"], successRate: 0.5);
        await actor.StoreSkillAsync(reliable);
        await actor.StoreSkillAsync(unreliable);

        var results = await actor.SearchAsync("deploy", options: new SkillSearchOptions { MinSuccessRate = 0.9 });

        results.Count.ShouldBe(1);
        results[0].Skill.SkillId.ShouldBe(reliable.SkillId);
    }

    [Fact]
    public async Task SearchAsync_PreferRecentBoostsRecentlyUsedSkill()
    {
        var now = new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var (actor, _) = CreateActor(fakeTime);
        var stale = CreateSkill(
            id: "stale",
            title: "Deploy service",
            tags: ["deploy"],
            lastUsedAt: now.AddDays(-60));
        var recent = CreateSkill(
            id: "recent",
            title: "Deploy service",
            tags: ["deploy"],
            lastUsedAt: now.AddDays(-2));
        await actor.StoreSkillAsync(stale);
        await actor.StoreSkillAsync(recent);

        var results = await actor.SearchAsync("deploy", options: new SkillSearchOptions { PreferRecent = true });

        results.Count.ShouldBe(2);
        results[0].Skill.SkillId.ShouldBe(recent.SkillId);
        results[0].RelevanceScore.ShouldBeGreaterThan(results[1].RelevanceScore);
    }

    [Fact]
    public async Task SearchAsync_WithoutPreferRecentKeepsExistingTieOrder()
    {
        var now = new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var (actor, _) = CreateActor(fakeTime);
        var stale = CreateSkill(
            id: "stale-default",
            title: "Deploy service",
            tags: ["deploy"],
            lastUsedAt: now.AddDays(-60));
        var recent = CreateSkill(
            id: "recent-default",
            title: "Deploy service",
            tags: ["deploy"],
            lastUsedAt: now.AddDays(-2));
        await actor.StoreSkillAsync(stale);
        await actor.StoreSkillAsync(recent);

        var results = await actor.SearchAsync("deploy");

        results.Count.ShouldBe(2);
        results[0].Skill.SkillId.ShouldBe(stale.SkillId);
        results[0].RelevanceScore.ShouldBe(results[1].RelevanceScore);
    }

    [Fact]
    public async Task RecordUsageAsync_IncrementsCount()
    {
        var (actor, _) = CreateActor();
        var skill = CreateSkill(id: "skill-usage");
        await actor.StoreSkillAsync(skill);

        await actor.RecordUsageAsync(skill.SkillId, success: true);

        var updated = await actor.GetSkillAsync(skill.SkillId);
        updated.ShouldNotBeNull();
        updated.UseCount.ShouldBe(1);
        updated.LastUsedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task RecordUsageAsync_UpdatesSuccessRate()
    {
        var (actor, _) = CreateActor();
        var skill = CreateSkill(id: "skill-rate", useCount: 0, successRate: 1.0);
        await actor.StoreSkillAsync(skill);

        // First usage: success -> rate = (1.0 * 0 + 1.0) / 1 = 1.0
        await actor.RecordUsageAsync(skill.SkillId, success: true);
        var afterFirst = await actor.GetSkillAsync(skill.SkillId);
        afterFirst.ShouldNotBeNull();
        afterFirst.SuccessRate.ShouldBe(1.0);

        // Second usage: failure -> rate = (1.0 * 1 + 0.0) / 2 = 0.5
        await actor.RecordUsageAsync(skill.SkillId, success: false);
        var afterSecond = await actor.GetSkillAsync(skill.SkillId);
        afterSecond.ShouldNotBeNull();
        afterSecond.SuccessRate.ShouldBe(0.5, 0.001);
    }

    [Fact]
    public async Task RemoveSkillAsync_DeletesSkill()
    {
        var (actor, _) = CreateActor();
        var skill = CreateSkill(id: "skill-remove");
        await actor.StoreSkillAsync(skill);

        await actor.RemoveSkillAsync(skill.SkillId);

        var result = await actor.GetSkillAsync(skill.SkillId);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetSkillAsync_ReturnsNull_WhenNotFound()
    {
        var (actor, _) = CreateActor();

        var result = await actor.GetSkillAsync(SkillId.From("nonexistent"));

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetAllSkillsAsync_ReturnsAllSkills()
    {
        var (actor, _) = CreateActor();
        var skill1 = CreateSkill(id: "skill-a", title: "First Skill");
        var skill2 = CreateSkill(id: "skill-b", title: "Second Skill");
        await actor.StoreSkillAsync(skill1);
        await actor.StoreSkillAsync(skill2);

        var all = await actor.GetAllSkillsAsync();

        all.Count.ShouldBe(2);
        all.ShouldContain(s => s.SkillId == skill1.SkillId);
        all.ShouldContain(s => s.SkillId == skill2.SkillId);
    }
}
