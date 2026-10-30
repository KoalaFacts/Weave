using Weave.Agents.Skills;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

public sealed class SkillMemoryStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 5, 12, 0, 0, TimeSpan.Zero);

    private static SkillDocument CreateSkill(
        string id = "skill-1",
        string title = "Deploy to Kubernetes",
        string description = "Steps to deploy a containerized application",
        List<string>? tags = null,
        int useCount = 0,
        double successRate = 1.0,
        DateTimeOffset? lastUsedAt = null,
        DateTimeOffset? archivedAt = null) =>
        new()
        {
            SkillId = SkillId.From(id),
            Title = title,
            Description = description,
            Tags = tags ?? ["deploy", "kubernetes"],
            Steps = [],
            ToolsUsed = [],
            CreatedByAgent = "agent",
            UseCount = useCount,
            SuccessRate = successRate,
            LastUsedAt = lastUsedAt,
            ArchivedAt = archivedAt
        };

    private static SkillMemoryState StateWith(params SkillDocument[] skills)
    {
        var state = new SkillMemoryState();
        foreach (var skill in skills)
            state.Skills[skill.SkillId.ToString()] = skill;
        return state;
    }

    [Fact]
    public void Search_EmptyQuery_ReturnsEmpty()
    {
        var state = StateWith(CreateSkill());

        var results = state.Search(string.Empty, maxResults: 10, options: null, now: Now);

        results.ShouldBeEmpty();
    }

    [Fact]
    public void Search_WhitespaceQuery_ReturnsEmpty()
    {
        var state = StateWith(CreateSkill());

        var results = state.Search("   \t  ", maxResults: 10, options: null, now: Now);

        results.ShouldBeEmpty();
    }

    [Fact]
    public void Search_PunctuationOnlyQuery_ReturnsEmpty()
    {
        var state = StateWith(CreateSkill());

        var results = state.Search("...,;:", maxResults: 10, options: null, now: Now);

        results.ShouldBeEmpty();
    }

    [Fact]
    public void Search_TagMatchScoresHigherThanTitle()
    {
        var tagOnly = CreateSkill(id: "tag-only", title: "Unrelated", description: "Unrelated", tags: ["deploy"]);
        var titleOnly = CreateSkill(id: "title-only", title: "Deploy guide", description: "Unrelated", tags: ["other"]);

        var results = StateWith(tagOnly, titleOnly).Search("deploy", maxResults: 10, options: null, now: Now);

        results.Count.ShouldBe(2);
        results[0].Skill.SkillId.ShouldBe(tagOnly.SkillId);
    }

    [Fact]
    public void Search_TitleMatchScoresHigherThanDescription()
    {
        var titleOnly = CreateSkill(id: "title-only", title: "Deploy guide", description: "Unrelated", tags: ["other"]);
        var descOnly = CreateSkill(id: "desc-only", title: "Unrelated", description: "How to deploy", tags: ["other"]);

        var results = StateWith(titleOnly, descOnly).Search("deploy", maxResults: 10, options: null, now: Now);

        results.Count.ShouldBe(2);
        results[0].Skill.SkillId.ShouldBe(titleOnly.SkillId);
    }

    [Fact]
    public void Search_ArchivedSkill_IsExcluded()
    {
        var archived = CreateSkill(id: "archived", archivedAt: Now);

        var results = StateWith(archived).Search("deploy", maxResults: 10, options: null, now: Now);

        results.ShouldBeEmpty();
    }

    [Fact]
    public void Search_RecencyBoost_AppliesAtSevenDayBoundary()
    {
        var atBoundary = CreateSkill(id: "boundary", tags: ["deploy"], lastUsedAt: Now.AddDays(-7));
        var beyondBoundary = CreateSkill(id: "beyond", tags: ["deploy"], lastUsedAt: Now.AddDays(-7).AddSeconds(-1));
        var options = new SkillSearchOptions { PreferRecent = true };

        var results = StateWith(atBoundary, beyondBoundary).Search("deploy", maxResults: 10, options, Now);

        results[0].Skill.SkillId.ShouldBe(atBoundary.SkillId);
        results[0].RelevanceScore.ShouldBeGreaterThan(results[1].RelevanceScore);
    }

    [Fact]
    public void Search_RecencyBoost_AppliesAtThirtyDayBoundary()
    {
        var atBoundary = CreateSkill(id: "boundary", tags: ["deploy"], lastUsedAt: Now.AddDays(-30));
        var beyondBoundary = CreateSkill(id: "beyond", tags: ["deploy"], lastUsedAt: Now.AddDays(-30).AddSeconds(-1));
        var options = new SkillSearchOptions { PreferRecent = true };

        var results = StateWith(atBoundary, beyondBoundary).Search("deploy", maxResults: 10, options, Now);

        results[0].Skill.SkillId.ShouldBe(atBoundary.SkillId);
        results[0].RelevanceScore.ShouldBeGreaterThan(results[1].RelevanceScore);
    }

    [Fact]
    public void Search_RecencyBoost_NotAppliedWhenLastUsedInFuture()
    {
        var future = CreateSkill(id: "future", tags: ["deploy"], lastUsedAt: Now.AddDays(1));
        var stale = CreateSkill(id: "stale", tags: ["deploy"], lastUsedAt: Now.AddDays(-100));
        var options = new SkillSearchOptions { PreferRecent = true };

        var results = StateWith(future, stale).Search("deploy", maxResults: 10, options, Now);

        results.Count.ShouldBe(2);
        results[0].RelevanceScore.ShouldBe(results[1].RelevanceScore);
    }

    [Fact]
    public void Search_RecencyBoost_NotAppliedWhenPreferRecentDisabled()
    {
        var recent = CreateSkill(id: "recent", tags: ["deploy"], lastUsedAt: Now.AddDays(-1));
        var stale = CreateSkill(id: "stale", tags: ["deploy"], lastUsedAt: Now.AddDays(-100));

        var results = StateWith(recent, stale).Search("deploy", maxResults: 10, options: null, now: Now);

        results.Count.ShouldBe(2);
        results[0].RelevanceScore.ShouldBe(results[1].RelevanceScore);
    }

    [Fact]
    public void Search_RecencyBoost_NotAppliedWhenLastUsedAtNull()
    {
        var unused = CreateSkill(id: "unused", tags: ["deploy"], lastUsedAt: null);
        var recent = CreateSkill(id: "recent", tags: ["deploy"], lastUsedAt: Now.AddDays(-1));
        var options = new SkillSearchOptions { PreferRecent = true };

        var results = StateWith(unused, recent).Search("deploy", maxResults: 10, options, Now);

        results.Count.ShouldBe(2);
        results[0].Skill.SkillId.ShouldBe(recent.SkillId);
    }

    [Fact]
    public void Search_MinSuccessRate_ClampsAboveOne()
    {
        var perfect = CreateSkill(id: "perfect", tags: ["deploy"], successRate: 1.0);
        var nearPerfect = CreateSkill(id: "near", tags: ["deploy"], successRate: 0.99);
        var options = new SkillSearchOptions { MinSuccessRate = 5.0 };

        var results = StateWith(perfect, nearPerfect).Search("deploy", maxResults: 10, options, Now);

        results.Count.ShouldBe(1);
        results[0].Skill.SkillId.ShouldBe(perfect.SkillId);
    }

    [Fact]
    public void Search_MinSuccessRate_ClampsBelowZero()
    {
        var any = CreateSkill(id: "any", tags: ["deploy"], successRate: 0.1);
        var options = new SkillSearchOptions { MinSuccessRate = -1.0 };

        var results = StateWith(any).Search("deploy", maxResults: 10, options, Now);

        results.Count.ShouldBe(1);
        results[0].Skill.SkillId.ShouldBe(any.SkillId);
    }

    [Fact]
    public void Search_SuccessRateMultipliesScore()
    {
        var lowSuccess = CreateSkill(id: "low", tags: ["deploy"], successRate: 0.25);
        var highSuccess = CreateSkill(id: "high", tags: ["deploy"], successRate: 1.0);

        var results = StateWith(lowSuccess, highSuccess).Search("deploy", maxResults: 10, options: null, now: Now);

        results.Count.ShouldBe(2);
        results[0].Skill.SkillId.ShouldBe(highSuccess.SkillId);
        results[0].RelevanceScore.ShouldBe(results[1].RelevanceScore * 4, tolerance: 1e-9);
    }

    [Fact]
    public void Search_RespectsMaxResults()
    {
        var skills = Enumerable.Range(0, 5)
            .Select(i => CreateSkill(id: $"skill-{i}", tags: ["deploy"], useCount: i))
            .ToArray();

        var results = StateWith(skills).Search("deploy", maxResults: 2, options: null, now: Now);

        results.Count.ShouldBe(2);
    }

    [Fact]
    public void Search_QueryIsCaseInsensitive()
    {
        var skill = CreateSkill(tags: ["DEPLOY"]);

        var results = StateWith(skill).Search("deploy", maxResults: 10, options: null, now: Now);

        results.Count.ShouldBe(1);
    }
}
