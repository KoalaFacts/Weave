using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

public sealed class UserModelGrainTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");
    private const string TestUserId = "user-42";

    private static IPersistentState<UserProfileState> CreatePersistentState()
    {
        var state = new UserProfileState
        {
            WorkspaceId = TestWorkspaceId.ToString(),
            UserId = TestUserId
        };

        var persistentState = Substitute.For<IPersistentState<UserProfileState>>();
        persistentState.State.Returns(state);
        persistentState.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync().Returns(Task.CompletedTask);
        persistentState.ClearStateAsync().Returns(Task.CompletedTask);
        return persistentState;
    }

    private static InteractionRecord CreateInteraction(
        string agentName = "researcher",
        string summary = "Discussed topic",
        List<string>? topics = null) =>
        new()
        {
            AgentName = agentName,
            Summary = summary,
            Topics = topics ?? ["testing"]
        };

    private static (UserModelGrain Grain, IEventBus EventBus) CreateGrain()
    {
        var eventBus = Substitute.For<IEventBus>();
        var logger = NullLogger<UserModelGrain>.Instance;
        var persistentState = CreatePersistentState();

        var grain = new UserModelGrain(eventBus, logger, persistentState);
        return (grain, eventBus);
    }

    [Fact]
    public async Task RecordInteractionAsync_AppendsInteraction()
    {
        var (grain, _) = CreateGrain();
        var record = CreateInteraction();

        await grain.RecordInteractionAsync(record);

        var profile = await grain.GetProfileAsync();
        profile.RecentInteractions.Count.ShouldBe(1);
        profile.RecentInteractions[0].AgentName.ShouldBe("researcher");
        profile.RecentInteractions[0].Summary.ShouldBe("Discussed topic");
    }

    [Fact]
    public async Task RecordInteractionAsync_UpdatesTopicFrequency()
    {
        var (grain, _) = CreateGrain();
        var record1 = CreateInteraction(topics: ["dotnet", "testing"]);
        var record2 = CreateInteraction(topics: ["dotnet", "deployment"]);

        await grain.RecordInteractionAsync(record1);
        await grain.RecordInteractionAsync(record2);

        var profile = await grain.GetProfileAsync();
        profile.TopicFrequency["dotnet"].ShouldBe(2);
        profile.TopicFrequency["testing"].ShouldBe(1);
        profile.TopicFrequency["deployment"].ShouldBe(1);
    }

    [Fact]
    public async Task RecordInteractionAsync_CapsAtMaxRecent()
    {
        var (grain, _) = CreateGrain();
        var profile = await grain.GetProfileAsync();
        profile.MaxRecentInteractions = 3;

        await grain.RecordInteractionAsync(CreateInteraction(summary: "First"));
        await grain.RecordInteractionAsync(CreateInteraction(summary: "Second"));
        await grain.RecordInteractionAsync(CreateInteraction(summary: "Third"));
        await grain.RecordInteractionAsync(CreateInteraction(summary: "Fourth"));

        profile = await grain.GetProfileAsync();
        profile.RecentInteractions.Count.ShouldBe(3);
        profile.RecentInteractions[0].Summary.ShouldBe("Second");
        profile.RecentInteractions[2].Summary.ShouldBe("Fourth");
    }

    [Fact]
    public async Task RecordInteractionAsync_SetsFirstSeenAt_OnFirstInteraction()
    {
        var (grain, _) = CreateGrain();

        var profileBefore = await grain.GetProfileAsync();
        profileBefore.FirstSeenAt.ShouldBeNull();

        await grain.RecordInteractionAsync(CreateInteraction());

        var profileAfter = await grain.GetProfileAsync();
        profileAfter.FirstSeenAt.ShouldNotBeNull();
        profileAfter.LastSeenAt.ShouldNotBeNull();

        var firstSeen = profileAfter.FirstSeenAt!.Value;

        await grain.RecordInteractionAsync(CreateInteraction());

        var profileLater = await grain.GetProfileAsync();
        profileLater.FirstSeenAt.ShouldBe(firstSeen);
        profileLater.TotalInteractions.ShouldBe(2);
    }

    [Fact]
    public async Task SetPreferenceAsync_StoresPreference()
    {
        var (grain, _) = CreateGrain();

        await grain.SetPreferenceAsync("theme", "dark");
        await grain.SetPreferenceAsync("verbosity", "concise");

        var profile = await grain.GetProfileAsync();
        profile.Preferences["theme"].ShouldBe("dark");
        profile.Preferences["verbosity"].ShouldBe("concise");
    }

    [Fact]
    public async Task SetDomainContextAsync_StoresContext()
    {
        var (grain, _) = CreateGrain();

        await grain.SetDomainContextAsync("language", "C#");
        await grain.SetDomainContextAsync("framework", "Orleans");

        var profile = await grain.GetProfileAsync();
        profile.DomainContext["language"].ShouldBe("C#");
        profile.DomainContext["framework"].ShouldBe("Orleans");
    }

    [Fact]
    public async Task GetContextSummaryAsync_IncludesPreferencesAndTopics()
    {
        var (grain, _) = CreateGrain();

        await grain.SetPreferenceAsync("theme", "dark");
        await grain.SetDomainContextAsync("language", "C#");
        await grain.RecordInteractionAsync(CreateInteraction(topics: ["testing", "grains"]));

        var summary = await grain.GetContextSummaryAsync();

        summary.ShouldContain("User preferences:");
        summary.ShouldContain("theme=dark");
        summary.ShouldContain("Top topics:");
        summary.ShouldContain("testing (1)");
        summary.ShouldContain("grains (1)");
        summary.ShouldContain("Domain context:");
        summary.ShouldContain("language=C#");
        summary.ShouldContain("Interactions: 1 total.");
    }

    [Fact]
    public async Task GetContextSummaryAsync_ReturnsEmpty_WhenNoData()
    {
        var (grain, _) = CreateGrain();

        var summary = await grain.GetContextSummaryAsync();

        summary.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task GetProfileAsync_ReturnsFullState()
    {
        var (grain, _) = CreateGrain();

        await grain.SetPreferenceAsync("key", "value");
        await grain.SetDomainContextAsync("domain", "ai");
        await grain.RecordInteractionAsync(CreateInteraction());

        var profile = await grain.GetProfileAsync();

        profile.UserId.ShouldBe(TestUserId);
        profile.WorkspaceId.ShouldBe(TestWorkspaceId.ToString());
        profile.Preferences.ShouldContainKey("key");
        profile.DomainContext.ShouldContainKey("domain");
        profile.RecentInteractions.Count.ShouldBe(1);
        profile.TotalInteractions.ShouldBe(1);
    }

    [Fact]
    public async Task ClearAsync_ResetsState()
    {
        var (grain, _) = CreateGrain();

        await grain.SetPreferenceAsync("theme", "dark");
        await grain.RecordInteractionAsync(CreateInteraction());

        await grain.ClearAsync();

        var profile = await grain.GetProfileAsync();
        profile.Preferences.ShouldBeEmpty();
        profile.RecentInteractions.ShouldBeEmpty();
        profile.TopicFrequency.ShouldBeEmpty();
        profile.DomainContext.ShouldBeEmpty();
        profile.TotalInteractions.ShouldBe(0);
        profile.FirstSeenAt.ShouldBeNull();
        profile.LastSeenAt.ShouldBeNull();
    }
}
