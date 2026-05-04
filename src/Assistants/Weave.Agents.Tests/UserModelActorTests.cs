using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Models;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Security.Events;
using Weave.Security.Tokens;
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

    private static CapabilityTokenService CreateTokenService() =>
        new(
            Options.Create(new CapabilityTokenOptions { SigningKey = "test-signing-key-that-is-at-least-32-chars-long" }),
            TimeProvider.System);

    private static CapabilityToken Token(CapabilityTokenService svc, params string[] grants) =>
        svc.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = TestWorkspaceId.ToString(),
            IssuedTo = "test",
            Grants = [.. grants],
            Lifetime = TimeSpan.FromHours(1)
        });

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

    private static (UserModelActor Actor, IEventBus EventBus, CapabilityToken Token) CreateActor()
    {
        var eventBus = Substitute.For<IEventBus>();
        var logger = NullLogger<UserModelActor>.Instance;
        var persistentState = CreatePersistentState();
        var tokenService = CreateTokenService();
        var authorizer = new CapabilityAuthorizer(tokenService, eventBus, NullLogger<CapabilityAuthorizer>.Instance);

        var actor = new UserModelActor(eventBus, TimeProvider.System, authorizer, logger, persistentState);
        return (actor, eventBus, Token(tokenService, $"user:read:{TestUserId}", $"user:write:{TestUserId}"));
    }

    [Fact]
    public async Task RecordInteractionAsync_AppendsInteraction()
    {
        var (actor, _, token) = CreateActor();
        var record = CreateInteraction();

        await actor.RecordInteractionAsync(record, token);

        var profile = await actor.GetProfileAsync(token);
        profile.RecentInteractions.Count.ShouldBe(1);
        profile.RecentInteractions[0].AgentName.ShouldBe("researcher");
        profile.RecentInteractions[0].Summary.ShouldBe("Discussed topic");
    }

    [Fact]
    public async Task RecordInteractionAsync_UpdatesTopicFrequency()
    {
        var (actor, _, token) = CreateActor();
        var record1 = CreateInteraction(topics: ["dotnet", "testing"]);
        var record2 = CreateInteraction(topics: ["dotnet", "deployment"]);

        await actor.RecordInteractionAsync(record1, token);
        await actor.RecordInteractionAsync(record2, token);

        var profile = await actor.GetProfileAsync(token);
        profile.TopicFrequency["dotnet"].ShouldBe(2);
        profile.TopicFrequency["testing"].ShouldBe(1);
        profile.TopicFrequency["deployment"].ShouldBe(1);
    }

    [Fact]
    public async Task RecordInteractionAsync_CapsAtMaxRecent()
    {
        var (actor, _, token) = CreateActor();
        var profile = await actor.GetProfileAsync(token);
        profile.MaxRecentInteractions = 3;

        await actor.RecordInteractionAsync(CreateInteraction(summary: "First"), token);
        await actor.RecordInteractionAsync(CreateInteraction(summary: "Second"), token);
        await actor.RecordInteractionAsync(CreateInteraction(summary: "Third"), token);
        await actor.RecordInteractionAsync(CreateInteraction(summary: "Fourth"), token);

        profile = await actor.GetProfileAsync(token);
        profile.RecentInteractions.Count.ShouldBe(3);
        profile.RecentInteractions[0].Summary.ShouldBe("Second");
        profile.RecentInteractions[2].Summary.ShouldBe("Fourth");
    }

    [Fact]
    public async Task RecordInteractionAsync_SetsFirstSeenAt_OnFirstInteraction()
    {
        var (actor, _, token) = CreateActor();

        var profileBefore = await actor.GetProfileAsync(token);
        profileBefore.FirstSeenAt.ShouldBeNull();

        await actor.RecordInteractionAsync(CreateInteraction(), token);

        var profileAfter = await actor.GetProfileAsync(token);
        profileAfter.FirstSeenAt.ShouldNotBeNull();
        profileAfter.LastSeenAt.ShouldNotBeNull();

        var firstSeen = profileAfter.FirstSeenAt!.Value;

        await actor.RecordInteractionAsync(CreateInteraction(), token);

        var profileLater = await actor.GetProfileAsync(token);
        profileLater.FirstSeenAt.ShouldBe(firstSeen);
        profileLater.TotalInteractions.ShouldBe(2);
    }

    [Fact]
    public async Task SetPreferenceAsync_StoresPreference()
    {
        var (actor, _, token) = CreateActor();

        await actor.SetPreferenceAsync("theme", "dark", token);
        await actor.SetPreferenceAsync("verbosity", "concise", token);

        var profile = await actor.GetProfileAsync(token);
        profile.Preferences["theme"].ShouldBe("dark");
        profile.Preferences["verbosity"].ShouldBe("concise");
    }

    [Fact]
    public async Task SetDomainContextAsync_StoresContext()
    {
        var (actor, _, token) = CreateActor();

        await actor.SetDomainContextAsync("language", "C#", token);
        await actor.SetDomainContextAsync("framework", "Orleans", token);

        var profile = await actor.GetProfileAsync(token);
        profile.DomainContext["language"].ShouldBe("C#");
        profile.DomainContext["framework"].ShouldBe("Orleans");
    }

    [Fact]
    public async Task GetContextSummaryAsync_IncludesPreferencesAndTopics()
    {
        var (actor, _, token) = CreateActor();

        await actor.SetPreferenceAsync("theme", "dark", token);
        await actor.SetDomainContextAsync("language", "C#", token);
        await actor.RecordInteractionAsync(CreateInteraction(topics: ["testing", "actors"]), token);

        var summary = await actor.GetContextSummaryAsync(token);

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
        var (actor, _, token) = CreateActor();

        var summary = await actor.GetContextSummaryAsync(token);

        summary.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task GetProfileAsync_ReturnsFullState()
    {
        var (actor, _, token) = CreateActor();

        await actor.SetPreferenceAsync("key", "value", token);
        await actor.SetDomainContextAsync("domain", "ai", token);
        await actor.RecordInteractionAsync(CreateInteraction(), token);

        var profile = await actor.GetProfileAsync(token);

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
        var (actor, _, token) = CreateActor();

        await actor.SetPreferenceAsync("theme", "dark", token);
        await actor.RecordInteractionAsync(CreateInteraction(), token);

        await actor.ClearAsync(token);

        var profile = await actor.GetProfileAsync(token);
        profile.Preferences.ShouldBeEmpty();
        profile.RecentInteractions.ShouldBeEmpty();
        profile.TopicFrequency.ShouldBeEmpty();
        profile.DomainContext.ShouldBeEmpty();
        profile.TotalInteractions.ShouldBe(0);
        profile.FirstSeenAt.ShouldBeNull();
        profile.LastSeenAt.ShouldBeNull();
    }

    [Fact]
    public async Task SetPreferenceAsync_OnDeniedWrite_PublishesEventWithUserWriteGrant()
    {
        var bus = new UserCapabilityCapturingEventBus();
        var tokenService = CreateTokenService();
        var authorizer = new CapabilityAuthorizer(tokenService, bus, NullLogger<CapabilityAuthorizer>.Instance);
        var persistentState = CreatePersistentState();
        var actor = new UserModelActor(bus, TimeProvider.System, authorizer, NullLogger<UserModelActor>.Instance, persistentState);
        var readOnlyToken = Token(tokenService, $"user:read:{TestUserId}");

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => actor.SetPreferenceAsync("theme", "dark", readOnlyToken));

        bus.CapabilityEvents.Count.ShouldBe(1);
        var evt = bus.CapabilityEvents[0];
        evt.Outcome.ShouldBe(CapabilityAuthorizationOutcome.Deny);
        evt.Reason.ShouldBe("grant-missing");
        evt.Grant.ShouldBe($"user:write:{TestUserId}");
        evt.ActionContext.ShouldBe("SetPreferenceAsync");
    }

    private sealed class UserCapabilityCapturingEventBus : IEventBus
    {
        public List<CapabilityAuthorizationEvent> CapabilityEvents { get; } = [];

        public Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct) where TEvent : IDomainEvent
        {
            if (domainEvent is CapabilityAuthorizationEvent capabilityEvent)
                CapabilityEvents.Add(capabilityEvent);
            return Task.CompletedTask;
        }

        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IDomainEvent =>
            throw new NotSupportedException();
    }
}
