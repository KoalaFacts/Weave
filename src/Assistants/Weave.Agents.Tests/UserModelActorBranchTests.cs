using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Events;

namespace Weave.Agents.Tests;

/// <summary>
/// Covers <see cref="UserModelActor"/> branches that <c>UserModelActorTests</c>
/// doesn't reach: <c>ApplyIdentity</c> when the state starts empty and must
/// derive the WorkspaceId/UserId from the actor key, plus the no-key fallback
/// branch (OnActivate without a primary key).
/// </summary>
public sealed class UserModelActorBranchTests
{
    private static IPersistentState<UserProfileState> EmptyState()
    {
        var state = new UserProfileState();
        var ps = Substitute.For<IPersistentState<UserProfileState>>();
        ps.State.Returns(state);
        ps.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        ps.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        ps.WriteStateAsync().Returns(Task.CompletedTask);
        return ps;
    }

    private static UserModelActor CreateActor(IPersistentState<UserProfileState> ps) => new(
        Substitute.For<IEventBus>(),
        TimeProvider.System,
        NullLogger<UserModelActor>.Instance,
        ps);

    [Fact]
    public async Task SetPreferenceAsync_EmptyState_NoKey_LeavesStateEmptyIdentity()
    {
        // Directly instantiated actor (no Orleans context) — TryGetPrimaryKeyString
        // throws NRE which the actor catches and returns null → ApplyIdentity
        // early-returns without mutating state.
        var ps = EmptyState();
        var actor = CreateActor(ps);

        await actor.SetPreferenceAsync("theme", "dark");

        // Identity was never applied (no key), but the preference still lands.
        ps.State.WorkspaceId.ShouldBe(string.Empty);
        ps.State.UserId.ShouldBe(string.Empty);
        ps.State.Preferences["theme"].ShouldBe("dark");
    }

    [Fact]
    public async Task SetDomainContextAsync_NewEntry_StoresValue()
    {
        var ps = EmptyState();
        ps.State.WorkspaceId = "ws-1";
        ps.State.UserId = "user-1";
        var actor = CreateActor(ps);

        await actor.SetDomainContextAsync("language", "C#");

        ps.State.DomainContext["language"].ShouldBe("C#");
    }

    [Fact]
    public async Task SetDomainContextAsync_OverwritesExisting()
    {
        var ps = EmptyState();
        ps.State.WorkspaceId = "ws-1";
        ps.State.UserId = "user-1";
        ps.State.DomainContext["team"] = "old-team";
        var actor = CreateActor(ps);

        await actor.SetDomainContextAsync("team", "new-team");

        ps.State.DomainContext["team"].ShouldBe("new-team");
    }

    [Fact]
    public async Task ClearAsync_ResetsAllFieldsIncludingMaxRecentAndPreferredModel()
    {
        var ps = EmptyState();
        ps.State.WorkspaceId = "ws-1";
        ps.State.UserId = "user-1";
        ps.State.TotalInteractions = 5;
        ps.State.PreferredModel = "gpt-4";
        ps.State.PreferredLanguage = "en";
        ps.State.MaxRecentInteractions = 50;
        ps.State.FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-10);
        ps.State.LastSeenAt = DateTimeOffset.UtcNow;

        var actor = CreateActor(ps);

        await actor.ClearAsync();

        ps.State.TotalInteractions.ShouldBe(0);
        ps.State.PreferredModel.ShouldBeNull();
        ps.State.PreferredLanguage.ShouldBeNull();
        ps.State.MaxRecentInteractions.ShouldBe(100, "ClearAsync resets the cap to the default 100");
        ps.State.FirstSeenAt.ShouldBeNull();
        ps.State.LastSeenAt.ShouldBeNull();
    }

    [Fact]
    public async Task GetContextSummaryAsync_OnlyTopicsNoPreferencesOrContext_IncludesTopTopics()
    {
        var ps = EmptyState();
        ps.State.WorkspaceId = "ws-1";
        ps.State.UserId = "user-1";
        ps.State.TotalInteractions = 3;
        ps.State.TopicFrequency["dotnet"] = 5;
        ps.State.TopicFrequency["orleans"] = 3;
        ps.State.TopicFrequency["testing"] = 2;

        var actor = CreateActor(ps);

        var summary = await actor.GetContextSummaryAsync();

        summary.ShouldContain("Top topics:");
        summary.ShouldContain("dotnet (5)");
        summary.ShouldNotContain("User preferences:");
        summary.ShouldNotContain("Domain context:");
    }

    [Fact]
    public async Task GetContextSummaryAsync_OnlyInteractions_IncludesTotalLine()
    {
        var ps = EmptyState();
        ps.State.WorkspaceId = "ws-1";
        ps.State.UserId = "user-1";
        ps.State.TotalInteractions = 42;

        var actor = CreateActor(ps);

        var summary = await actor.GetContextSummaryAsync();

        summary.ShouldContain("Interactions: 42 total.");
    }
}
