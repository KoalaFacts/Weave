using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Events;

namespace Weave.Agents.Tests;

/// <summary>
/// Covers <see cref="UserModelGrain"/> branches that <c>UserModelGrainTests</c>
/// doesn't reach: <c>ApplyIdentity</c> when the state starts empty and must
/// derive the WorkspaceId/UserId from the grain key, plus the no-key fallback
/// branch (OnActivate without a primary key).
/// </summary>
public sealed class UserModelGrainBranchTests
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

    private static UserModelGrain CreateGrain(IPersistentState<UserProfileState> ps) => new(
        Substitute.For<IEventBus>(),
        TimeProvider.System,
        NullLogger<UserModelGrain>.Instance,
        ps);

    [Fact]
    public async Task SetPreferenceAsync_EmptyState_NoKey_LeavesStateEmptyIdentity()
    {
        // Directly instantiated grain (no Orleans context) — TryGetPrimaryKeyString
        // throws NRE which the grain catches and returns null → ApplyIdentity
        // early-returns without mutating state.
        var ps = EmptyState();
        var grain = CreateGrain(ps);

        await grain.SetPreferenceAsync("theme", "dark");

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
        var grain = CreateGrain(ps);

        await grain.SetDomainContextAsync("language", "C#");

        ps.State.DomainContext["language"].ShouldBe("C#");
    }

    [Fact]
    public async Task SetDomainContextAsync_OverwritesExisting()
    {
        var ps = EmptyState();
        ps.State.WorkspaceId = "ws-1";
        ps.State.UserId = "user-1";
        ps.State.DomainContext["team"] = "old-team";
        var grain = CreateGrain(ps);

        await grain.SetDomainContextAsync("team", "new-team");

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

        var grain = CreateGrain(ps);

        await grain.ClearAsync();

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

        var grain = CreateGrain(ps);

        var summary = await grain.GetContextSummaryAsync();

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

        var grain = CreateGrain(ps);

        var summary = await grain.GetContextSummaryAsync();

        summary.ShouldContain("Interactions: 42 total.");
    }
}
