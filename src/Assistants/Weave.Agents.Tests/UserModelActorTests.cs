using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

public sealed class UserModelActorTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");
    private const string TestUserId = "user-42";

    private static IActorState<UserProfileState> CreatePersistentState()
    {
        var state = new UserProfileState
        {
            WorkspaceId = TestWorkspaceId.ToString(),
            UserId = TestUserId
        };

        var persistentState = Substitute.For<IActorState<UserProfileState>>();
        persistentState.State.Returns(state);
        persistentState.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.ClearStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
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

    private static (UserModelActor Actor, IEventBus EventBus) CreateActor()
    {
        var eventBus = Substitute.For<IEventBus>();
        var logger = NullLogger<UserModelActor>.Instance;
        var persistentState = CreatePersistentState();

        var actor = new UserModelActor(eventBus, TimeProvider.System, logger, persistentState);
        return (actor, eventBus);
    }

    [Fact]
    public async Task RecordInteractionAsync_AppendsInteraction()
    {
        var (actor, _) = CreateActor();
        var record = CreateInteraction();

        await actor.RecordInteractionAsync(record);

        var profile = await actor.GetProfileAsync();
        profile.RecentInteractions.Count.ShouldBe(1);
        profile.RecentInteractions[0].AgentName.ShouldBe("researcher");
        profile.RecentInteractions[0].Summary.ShouldBe("Discussed topic");
    }

    [Fact]
    public async Task RecordInteractionAsync_UpdatesTopicFrequency()
    {
        var (actor, _) = CreateActor();
        var record1 = CreateInteraction(topics: ["dotnet", "testing"]);
        var record2 = CreateInteraction(topics: ["dotnet", "deployment"]);

        await actor.RecordInteractionAsync(record1);
        await actor.RecordInteractionAsync(record2);

        var profile = await actor.GetProfileAsync();
        profile.TopicFrequency["dotnet"].ShouldBe(2);
        profile.TopicFrequency["testing"].ShouldBe(1);
        profile.TopicFrequency["deployment"].ShouldBe(1);
    }

    [Fact]
    public async Task RecordInteractionAsync_CapsAtMaxRecent()
    {
        var (actor, _) = CreateActor();
        var profile = await actor.GetProfileAsync();
        profile.MaxRecentInteractions = 3;

        await actor.RecordInteractionAsync(CreateInteraction(summary: "First"));
        await actor.RecordInteractionAsync(CreateInteraction(summary: "Second"));
        await actor.RecordInteractionAsync(CreateInteraction(summary: "Third"));
        await actor.RecordInteractionAsync(CreateInteraction(summary: "Fourth"));

        profile = await actor.GetProfileAsync();
        profile.RecentInteractions.Count.ShouldBe(3);
        profile.RecentInteractions[0].Summary.ShouldBe("Second");
        profile.RecentInteractions[2].Summary.ShouldBe("Fourth");
    }

    [Fact]
    public async Task RecordInteractionAsync_SetsFirstSeenAt_OnFirstInteraction()
    {
        var (actor, _) = CreateActor();

        var profileBefore = await actor.GetProfileAsync();
        profileBefore.FirstSeenAt.ShouldBeNull();

        await actor.RecordInteractionAsync(CreateInteraction());

        var profileAfter = await actor.GetProfileAsync();
        profileAfter.FirstSeenAt.ShouldNotBeNull();
        profileAfter.LastSeenAt.ShouldNotBeNull();

        var firstSeen = profileAfter.FirstSeenAt!.Value;

        await actor.RecordInteractionAsync(CreateInteraction());

        var profileLater = await actor.GetProfileAsync();
        profileLater.FirstSeenAt.ShouldBe(firstSeen);
        profileLater.TotalInteractions.ShouldBe(2);
    }

    [Fact]
    public async Task SetPreferenceAsync_StoresPreference()
    {
        var (actor, _) = CreateActor();

        await actor.SetPreferenceAsync("theme", "dark");
        await actor.SetPreferenceAsync("verbosity", "concise");

        var profile = await actor.GetProfileAsync();
        profile.Preferences["theme"].ShouldBe("dark");
        profile.Preferences["verbosity"].ShouldBe("concise");
    }

    [Fact]
    public async Task SetDomainContextAsync_StoresContext()
    {
        var (actor, _) = CreateActor();

        await actor.SetDomainContextAsync("language", "C#");
        await actor.SetDomainContextAsync("framework", "Orleans");

        var profile = await actor.GetProfileAsync();
        profile.DomainContext["language"].ShouldBe("C#");
        profile.DomainContext["framework"].ShouldBe("Orleans");
    }

    [Fact]
    public async Task GetContextSummaryAsync_IncludesPreferencesAndTopics()
    {
        var (actor, _) = CreateActor();

        await actor.SetPreferenceAsync("theme", "dark");
        await actor.SetDomainContextAsync("language", "C#");
        await actor.RecordInteractionAsync(CreateInteraction(topics: ["testing", "actors"]));

        var summary = await actor.GetContextSummaryAsync();

        summary.ShouldContain("User preferences:");
        summary.ShouldContain("theme=dark");
        summary.ShouldContain("Top topics:");
        summary.ShouldContain("testing (1)");
        summary.ShouldContain("actors (1)");
        summary.ShouldContain("Domain context:");
        summary.ShouldContain("language=C#");
        summary.ShouldContain("Interactions: 1 total.");
    }

    [Fact]
    public async Task GetContextSummaryAsync_ReturnsEmpty_WhenNoData()
    {
        var (actor, _) = CreateActor();

        var summary = await actor.GetContextSummaryAsync();

        summary.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task GetProfileAsync_ReturnsFullState()
    {
        var (actor, _) = CreateActor();

        await actor.SetPreferenceAsync("key", "value");
        await actor.SetDomainContextAsync("domain", "ai");
        await actor.RecordInteractionAsync(CreateInteraction());

        var profile = await actor.GetProfileAsync();

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
        var (actor, _) = CreateActor();

        await actor.SetPreferenceAsync("theme", "dark");
        await actor.RecordInteractionAsync(CreateInteraction());

        await actor.ClearAsync();

        var profile = await actor.GetProfileAsync();
        profile.Preferences.ShouldBeEmpty();
        profile.RecentInteractions.ShouldBeEmpty();
        profile.TopicFrequency.ShouldBeEmpty();
        profile.DomainContext.ShouldBeEmpty();
        profile.TotalInteractions.ShouldBe(0);
        profile.FirstSeenAt.ShouldBeNull();
        profile.LastSeenAt.ShouldBeNull();
    }
}
