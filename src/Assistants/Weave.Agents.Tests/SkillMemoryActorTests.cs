using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Security.Tokens;
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

    private static CapabilityTokenService CreateTokenService(TimeProvider? timeProvider = null) =>
        new CapabilityTokenService(
            Options.Create(new CapabilityTokenOptions { SigningKey = "test-signing-key-that-is-at-least-32-chars-long" }),
            timeProvider ?? TimeProvider.System);

    private static CapabilityToken Token(CapabilityTokenService svc, params string[] grants) =>
        svc.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = TestWorkspaceId.ToString(),
            IssuedTo = "test",
            Grants = [.. grants],
            Lifetime = TimeSpan.FromHours(1)
        });

    private static CapabilityAuthorizer CreateAuthorizer(CapabilityTokenService tokenService, IEventBus bus) =>
        new(tokenService, bus, NullLogger<CapabilityAuthorizer>.Instance);

    private static (SkillMemoryActor Actor, IEventBus EventBus, CapabilityToken Token) CreateActor(TimeProvider? timeProvider = null)
    {
        var eventBus = Substitute.For<IEventBus>();
        var logger = NullLogger<SkillMemoryActor>.Instance;
        var persistentState = CreatePersistentState();
        var tokenService = CreateTokenService(timeProvider);
        var authorizer = CreateAuthorizer(tokenService, eventBus);

        var actor = new SkillMemoryActor(eventBus, timeProvider ?? TimeProvider.System, authorizer, logger, persistentState);
        return (actor, eventBus, Token(tokenService, "skill:read", "skill:write"));
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
        var (actor, _, token) = CreateActor();
        var skill = CreateSkill(id: "skill-1");

        var result = await actor.StoreSkillAsync(skill, token);

        result.ShouldNotBeNull();
        result.SkillId.ShouldBe(skill.SkillId);
        result.Title.ShouldBe("Deploy to Kubernetes");

        var retrieved = await actor.GetSkillAsync(skill.SkillId, token);
        retrieved.ShouldNotBeNull();
        retrieved.SkillId.ShouldBe(skill.SkillId);
    }

    [Fact]
    public async Task SuggestSkillAsync_AddsPendingSuggestionWithoutSearchableSkill()
    {
        var (actor, _, token) = CreateActor();
        var skill = CreateSkill(id: "suggested-skill", title: "Suggested deployment skill");

        var suggestion = await actor.SuggestSkillAsync(skill, token, "task-1");

        suggestion.Skill.SkillId.ShouldBe(skill.SkillId);
        suggestion.SourceTaskId.ShouldBe("task-1");
        var pending = await actor.GetSuggestedSkillsAsync(token);
        pending.Count.ShouldBe(1);
        var stored = await actor.GetSkillAsync(skill.SkillId, token);
        stored.ShouldBeNull();
    }

    [Fact]
    public async Task AcceptSuggestedSkillAsync_MovesSuggestionIntoSkills()
    {
        var (actor, _, token) = CreateActor();
        var skill = CreateSkill(id: "accepted-suggestion", title: "Accepted skill");
        await actor.SuggestSkillAsync(skill, token, "task-2");

        var accepted = await actor.AcceptSuggestedSkillAsync(skill.SkillId, token);

        accepted.ShouldNotBeNull();
        accepted.SkillId.ShouldBe(skill.SkillId);
        var pending = await actor.GetSuggestedSkillsAsync(token);
        pending.ShouldBeEmpty();
        var stored = await actor.GetSkillAsync(skill.SkillId, token);
        stored.ShouldNotBeNull();
    }

    [Fact]
    public async Task RejectSuggestedSkillAsync_RemovesSuggestionWithoutStoringSkill()
    {
        var (actor, _, token) = CreateActor();
        var skill = CreateSkill(id: "rejected-suggestion", title: "Rejected skill");
        await actor.SuggestSkillAsync(skill, token, "task-3");

        var rejected = await actor.RejectSuggestedSkillAsync(skill.SkillId, token);

        rejected.ShouldBeTrue();
        var pending = await actor.GetSuggestedSkillsAsync(token);
        pending.ShouldBeEmpty();
        var stored = await actor.GetSkillAsync(skill.SkillId, token);
        stored.ShouldBeNull();
    }

    [Fact]
    public async Task AcceptSuggestedSkillAsync_ReturnsNull_WhenSuggestionIsMissing()
    {
        var (actor, _, token) = CreateActor();

        var accepted = await actor.AcceptSuggestedSkillAsync(SkillId.From("missing-suggestion"), token);

        accepted.ShouldBeNull();
    }

    [Fact]
    public async Task RejectSuggestedSkillAsync_ReturnsFalse_WhenSuggestionIsMissing()
    {
        var (actor, _, token) = CreateActor();

        var rejected = await actor.RejectSuggestedSkillAsync(SkillId.From("missing-suggestion"), token);

        rejected.ShouldBeFalse();
    }

    [Fact]
    public async Task ArchiveSkillAsync_MarksSkillArchivedAndExcludesFromSearchAndList()
    {
        var (actor, _, token) = CreateActor();
        var skill = CreateSkill(id: "archived-skill", title: "Archive deployment skill", tags: ["archive"]);
        await actor.StoreSkillAsync(skill, token);

        var archived = await actor.ArchiveSkillAsync(skill.SkillId, token);

        archived.ShouldNotBeNull();
        archived.ArchivedAt.ShouldNotBeNull();
        var searchResults = await actor.SearchAsync("archive", token);
        searchResults.ShouldBeEmpty();
        var allSkills = await actor.GetAllSkillsAsync(token);
        allSkills.ShouldBeEmpty();
        var direct = await actor.GetSkillAsync(skill.SkillId, token);
        direct.ShouldNotBeNull();
        direct.ArchivedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task ArchiveSkillAsync_ReturnsNull_WhenSkillIsMissing()
    {
        var (actor, _, token) = CreateActor();

        var archived = await actor.ArchiveSkillAsync(SkillId.From("missing-archive"), token);

        archived.ShouldBeNull();
    }

    [Fact]
    public async Task RestoreSkillAsync_ClearsArchiveAndIncludesInSearchAndList()
    {
        var (actor, _, token) = CreateActor();
        var skill = CreateSkill(id: "restored-skill", title: "Restore deployment skill", tags: ["restore"]);
        await actor.StoreSkillAsync(skill, token);
        await actor.ArchiveSkillAsync(skill.SkillId, token);

        var restored = await actor.RestoreSkillAsync(skill.SkillId, token);

        restored.ShouldNotBeNull();
        restored.ArchivedAt.ShouldBeNull();
        var searchResults = await actor.SearchAsync("restore", token);
        searchResults.Count.ShouldBe(1);
        searchResults[0].Skill.SkillId.ShouldBe(skill.SkillId);
        var allSkills = await actor.GetAllSkillsAsync(token);
        allSkills.Count.ShouldBe(1);
        allSkills[0].SkillId.ShouldBe(skill.SkillId);
    }

    [Fact]
    public async Task RestoreSkillAsync_ReturnsNull_WhenSkillIsMissing()
    {
        var (actor, _, token) = CreateActor();

        var restored = await actor.RestoreSkillAsync(SkillId.From("missing-restore"), token);

        restored.ShouldBeNull();
    }

    [Fact]
    public async Task SearchAsync_MatchesByTags()
    {
        var (actor, _, token) = CreateActor();
        var skill = CreateSkill(tags: ["deploy", "kubernetes", "containers"]);
        await actor.StoreSkillAsync(skill, token);

        var results = await actor.SearchAsync("kubernetes", token);

        results.ShouldNotBeEmpty();
        results[0].Skill.SkillId.ShouldBe(skill.SkillId);
        results[0].RelevanceScore.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task SearchAsync_MatchesByTitleKeywords()
    {
        var (actor, _, token) = CreateActor();
        var skill = CreateSkill(title: "Automated database migration");
        await actor.StoreSkillAsync(skill, token);

        var results = await actor.SearchAsync("database migration", token);

        results.ShouldNotBeEmpty();
        results[0].Skill.SkillId.ShouldBe(skill.SkillId);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmpty_WhenNoMatch()
    {
        var (actor, _, token) = CreateActor();
        var skill = CreateSkill(
            title: "Deploy to Kubernetes",
            description: "Kubernetes deployment steps",
            tags: ["deploy", "kubernetes"]);
        await actor.StoreSkillAsync(skill, token);

        var results = await actor.SearchAsync("quantum entanglement", token);

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_RanksHigherUseCountFirst()
    {
        var (actor, _, token) = CreateActor();

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

        await actor.StoreSkillAsync(lowUse, token);
        await actor.StoreSkillAsync(highUse, token);

        var results = await actor.SearchAsync("deploy", token);

        results.Count.ShouldBeGreaterThanOrEqualTo(2);
        results[0].Skill.SkillId.ShouldBe(highUse.SkillId);
        results[0].RelevanceScore.ShouldBeGreaterThan(results[1].RelevanceScore);
    }

    [Fact]
    public async Task SearchAsync_FiltersByMinimumSuccessRate()
    {
        var (actor, _, token) = CreateActor();
        var reliable = CreateSkill(id: "reliable", title: "Deploy app", tags: ["deploy"], successRate: 0.95);
        var unreliable = CreateSkill(id: "unreliable", title: "Deploy app", tags: ["deploy"], successRate: 0.5);
        await actor.StoreSkillAsync(reliable, token);
        await actor.StoreSkillAsync(unreliable, token);

        var results = await actor.SearchAsync("deploy", token, options: new SkillSearchOptions { MinSuccessRate = 0.9 });

        results.Count.ShouldBe(1);
        results[0].Skill.SkillId.ShouldBe(reliable.SkillId);
    }

    [Fact]
    public async Task SearchAsync_PreferRecentBoostsRecentlyUsedSkill()
    {
        var now = new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var (actor, _, token) = CreateActor(fakeTime);
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
        await actor.StoreSkillAsync(stale, token);
        await actor.StoreSkillAsync(recent, token);

        var results = await actor.SearchAsync("deploy", token, options: new SkillSearchOptions { PreferRecent = true });

        results.Count.ShouldBe(2);
        results[0].Skill.SkillId.ShouldBe(recent.SkillId);
        results[0].RelevanceScore.ShouldBeGreaterThan(results[1].RelevanceScore);
    }

    [Fact]
    public async Task SearchAsync_WithoutPreferRecentKeepsExistingTieOrder()
    {
        var now = new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var (actor, _, token) = CreateActor(fakeTime);
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
        await actor.StoreSkillAsync(stale, token);
        await actor.StoreSkillAsync(recent, token);

        var results = await actor.SearchAsync("deploy", token);

        results.Count.ShouldBe(2);
        results[0].Skill.SkillId.ShouldBe(stale.SkillId);
        results[0].RelevanceScore.ShouldBe(results[1].RelevanceScore);
    }

    [Fact]
    public async Task RecordUsageAsync_IncrementsCount()
    {
        var (actor, _, token) = CreateActor();
        var skill = CreateSkill(id: "skill-usage");
        await actor.StoreSkillAsync(skill, token);

        await actor.RecordUsageAsync(skill.SkillId, success: true, token);

        var updated = await actor.GetSkillAsync(skill.SkillId, token);
        updated.ShouldNotBeNull();
        updated.UseCount.ShouldBe(1);
        updated.LastUsedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task RecordUsageAsync_UpdatesSuccessRate()
    {
        var (actor, _, token) = CreateActor();
        var skill = CreateSkill(id: "skill-rate", useCount: 0, successRate: 1.0);
        await actor.StoreSkillAsync(skill, token);

        // First usage: success -> rate = (1.0 * 0 + 1.0) / 1 = 1.0
        await actor.RecordUsageAsync(skill.SkillId, success: true, token);
        var afterFirst = await actor.GetSkillAsync(skill.SkillId, token);
        afterFirst.ShouldNotBeNull();
        afterFirst.SuccessRate.ShouldBe(1.0);

        // Second usage: failure -> rate = (1.0 * 1 + 0.0) / 2 = 0.5
        await actor.RecordUsageAsync(skill.SkillId, success: false, token);
        var afterSecond = await actor.GetSkillAsync(skill.SkillId, token);
        afterSecond.ShouldNotBeNull();
        afterSecond.SuccessRate.ShouldBe(0.5, 0.001);
    }

    [Fact]
    public async Task RemoveSkillAsync_DeletesSkill()
    {
        var (actor, _, token) = CreateActor();
        var skill = CreateSkill(id: "skill-remove");
        await actor.StoreSkillAsync(skill, token);

        await actor.RemoveSkillAsync(skill.SkillId, token);

        var result = await actor.GetSkillAsync(skill.SkillId, token);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetSkillAsync_ReturnsNull_WhenNotFound()
    {
        var (actor, _, token) = CreateActor();

        var result = await actor.GetSkillAsync(SkillId.From("nonexistent"), token);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetAllSkillsAsync_ReturnsAllSkills()
    {
        var (actor, _, token) = CreateActor();
        var skill1 = CreateSkill(id: "skill-a", title: "First Skill");
        var skill2 = CreateSkill(id: "skill-b", title: "Second Skill");
        await actor.StoreSkillAsync(skill1, token);
        await actor.StoreSkillAsync(skill2, token);

        var all = await actor.GetAllSkillsAsync(token);

        all.Count.ShouldBe(2);
        all.ShouldContain(s => s.SkillId == skill1.SkillId);
        all.ShouldContain(s => s.SkillId == skill2.SkillId);
    }

    [Fact]
    public async Task GetSuggestedSkillsAsync_OrdersByMostRecentlySuggestedFirst()
    {
        var now = new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var (actor, _, token) = CreateActor(fakeTime);

        await actor.SuggestSkillAsync(CreateSkill(id: "older", title: "Older suggestion"), token, "task-older");
        fakeTime.Advance(TimeSpan.FromMinutes(5));
        await actor.SuggestSkillAsync(CreateSkill(id: "newer", title: "Newer suggestion"), token, "task-newer");

        var suggestions = await actor.GetSuggestedSkillsAsync(token);

        suggestions.Count.ShouldBe(2);
        suggestions[0].Skill.SkillId.ToString().ShouldBe("newer");
        suggestions[1].Skill.SkillId.ToString().ShouldBe("older");
    }

    [Fact]
    public async Task RecordUsageAsync_OnMissingSkill_DoesNotThrow()
    {
        var (actor, _, token) = CreateActor();

        // The skill was never stored — should silently no-op.
        await actor.RecordUsageAsync(SkillId.From("never-stored"), success: true, token);

        var found = await actor.GetSkillAsync(SkillId.From("never-stored"), token);
        found.ShouldBeNull();
    }

    [Fact]
    public async Task SearchAsync_WithEmptyQueryAfterTokenization_ReturnsEmpty()
    {
        var (actor, _, token) = CreateActor();
        await actor.StoreSkillAsync(CreateSkill(id: "any", title: "Anything"), token);

        var results = await actor.SearchAsync("   ", token);

        results.ShouldBeEmpty();
    }

    // --- Capability check tests ---

    private static SkillMemoryActor BuildActor(CapabilityTokenService tokenService, IEventBus? bus = null)
    {
        var eventBus = bus ?? Substitute.For<IEventBus>();
        var authorizer = CreateAuthorizer(tokenService, eventBus);
        return new SkillMemoryActor(
            eventBus,
            TimeProvider.System,
            authorizer,
            NullLogger<SkillMemoryActor>.Instance,
            CreatePersistentState());
    }

    [Fact]
    public async Task StoreSkillAsync_WithoutSkillWriteGrant_Throws()
    {
        var tokenService = CreateTokenService();
        var actor = BuildActor(tokenService);
        var readOnlyToken = Token(tokenService, "skill:read");

        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            actor.StoreSkillAsync(CreateSkill(), readOnlyToken));
    }

    [Fact]
    public async Task SearchAsync_WithoutSkillReadGrant_Throws()
    {
        var tokenService = CreateTokenService();
        var actor = BuildActor(tokenService);
        var writeOnlyToken = Token(tokenService, "skill:write");

        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            actor.SearchAsync("anything", writeOnlyToken));
    }

    [Fact]
    public async Task StoreSkillAsync_WithExpiredToken_Throws()
    {
        var tokenService = CreateTokenService();
        var actor = BuildActor(tokenService);
        var expired = tokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = TestWorkspaceId.ToString(),
            IssuedTo = "test",
            Grants = ["skill:write"],
            Lifetime = TimeSpan.FromMilliseconds(-1)
        });

        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            actor.StoreSkillAsync(CreateSkill(), expired));
    }

    [Fact]
    public async Task GetSkillAsync_WithWildcardGrant_Allowed()
    {
        var tokenService = CreateTokenService();
        var actor = BuildActor(tokenService);
        var wildcard = Token(tokenService, "*");

        var result = await actor.GetSkillAsync(SkillId.From("anything"), wildcard);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task StoreSkillAsync_WithCrossWorkspaceToken_Throws()
    {
        var tokenService = CreateTokenService();
        var actor = BuildActor(tokenService);
        var foreignToken = tokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "other-workspace",
            IssuedTo = "attacker",
            Grants = ["skill:write"],
            Lifetime = TimeSpan.FromHours(1)
        });

        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            actor.StoreSkillAsync(CreateSkill(), foreignToken));
    }

    [Fact]
    public async Task StoreSkillAsync_OnDeniedWrite_PublishesEventWithSkillWriteGrant()
    {
        var tokenService = CreateTokenService();
        var bus = new SkillCapabilityCapturingEventBus();
        var actor = BuildActor(tokenService, bus);
        var readOnlyToken = Token(tokenService, "skill:read");

        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            actor.StoreSkillAsync(CreateSkill(), readOnlyToken));

        bus.CapabilityEvents.Count.ShouldBe(1);
        var evt = bus.CapabilityEvents[0];
        evt.Outcome.ShouldBe(Weave.Security.Events.CapabilityAuthorizationOutcome.Deny);
        evt.Reason.ShouldBe("grant-missing");
        evt.Grant.ShouldBe("skill:write");
        evt.ActionContext.ShouldBe("StoreSkillAsync");
    }

    private sealed class SkillCapabilityCapturingEventBus : IEventBus
    {
        public List<Weave.Security.Events.CapabilityAuthorizationEvent> CapabilityEvents { get; } = [];

        public Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct) where TEvent : IDomainEvent
        {
            if (domainEvent is Weave.Security.Events.CapabilityAuthorizationEvent capabilityEvent)
                CapabilityEvents.Add(capabilityEvent);
            return Task.CompletedTask;
        }

        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IDomainEvent =>
            throw new NotSupportedException();
    }
}
