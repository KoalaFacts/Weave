using Weave.Agents.Commands;
using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Agents.Queries;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

/// <summary>
/// Covers the thin command/query handlers in <c>Weave.Agents</c> that
/// delegate to a actor. Each handler is one Arrange-Act-Assert: wire
/// up a substituted actor via <see cref="IActorFactory"/>, invoke the
/// handler, assert the actor call was made with the right arguments
/// and the returned value flows through.
///
/// These handlers looked like coverage gaps because the Silo
/// integration tests exercise them through CQRS dispatch but the
/// coverage tool credits lines to the owning test project —
/// <c>Weave.Agents</c> needs <c>Weave.Agents.Tests</c> coverage.
/// </summary>
public sealed class CqrsHandlerTests
{
    private static readonly WorkspaceId Ws = WorkspaceId.From("ws-1");

    [Fact]
    public async Task SendAgentMessageHandler_DelegatesToAgentActor()
    {
        var factory = Substitute.For<IActorFactory>();
        var agent = Substitute.For<IAgentActor>();
        var expected = new AgentChatResponse { Content = "hi back", ConversationId = "c1", UsedTools = false };
        factory.GetGrain<IAgentActor>("ws-1/agent-a", null).Returns(agent);
        agent.SendAsync(Arg.Any<AgentMessage>()).Returns(expected);

        var handler = new SendAgentMessageHandler(new TestVirtualActorProvider(factory));
        var response = await handler.HandleAsync(
            new SendAgentMessageCommand(Ws, "agent-a", new AgentMessage { Role = "user", Content = "hi" }),
            TestContext.Current.CancellationToken);

        response.ShouldBe(expected);
    }

    [Fact]
    public async Task SetUserPreferenceHandler_CallsActorSetPreferenceAndReturnsTrue()
    {
        var factory = Substitute.For<IActorFactory>();
        var user = Substitute.For<IUserModelActor>();
        factory.GetGrain<IUserModelActor>("ws-1/user-1", null).Returns(user);

        var handler = new SetUserPreferenceHandler(new TestVirtualActorProvider(factory));
        var result = await handler.HandleAsync(
            new SetUserPreferenceCommand(Ws, "user-1", "theme", "dark"),
            TestContext.Current.CancellationToken);

        result.ShouldBeTrue();
        await user.Received(1).SetPreferenceAsync("theme", "dark");
    }

    [Fact]
    public async Task StoreSkillHandler_DelegatesToSkillMemoryActor()
    {
        var factory = Substitute.For<IActorFactory>();
        var memory = Substitute.For<ISkillMemoryActor>();
        var skill = new SkillDocument
        {
            SkillId = SkillId.New(),
            Title = "t",
            Description = "d",
            Tags = [],
            Steps = [],
            ToolsUsed = [],
            CreatedByAgent = "agent-1"
        };
        factory.GetGrain<ISkillMemoryActor>("ws-1", null).Returns(memory);
        memory.StoreSkillAsync(skill).Returns(skill);

        var handler = new StoreSkillHandler(new TestVirtualActorProvider(factory));
        var stored = await handler.HandleAsync(
            new StoreSkillCommand(Ws, skill),
            TestContext.Current.CancellationToken);

        stored.SkillId.ShouldBe(skill.SkillId);
    }

    [Fact]
    public async Task RegisterChannelHandler_CallsActorRegisterChannelAndReturnsTrue()
    {
        var factory = Substitute.For<IActorFactory>();
        var gateway = Substitute.For<IChannelGatewayActor>();
        factory.GetGrain<IChannelGatewayActor>("ws-1", null).Returns(gateway);
        var config = new ChannelConfig
        {
            ChannelId = ChannelId.New(),
            Type = ChannelType.Slack,
            Name = "slack-main"
        };

        var handler = new RegisterChannelHandler(new TestVirtualActorProvider(factory));
        var result = await handler.HandleAsync(
            new RegisterChannelCommand(Ws, config),
            TestContext.Current.CancellationToken);

        result.ShouldBeTrue();
        await gateway.Received(1).RegisterChannelAsync(config);
    }

    [Fact]
    public async Task RouteInboundMessageHandler_ReturnsOutboundMessageFromActor()
    {
        var factory = Substitute.For<IActorFactory>();
        var gateway = Substitute.For<IChannelGatewayActor>();
        var outbound = new OutboundMessage { ChannelId = ChannelId.New(), Content = "response" };
        factory.GetGrain<IChannelGatewayActor>("ws-1", null).Returns(gateway);
        gateway.RouteInboundAsync(Arg.Any<InboundMessage>()).Returns(outbound);

        var handler = new RouteInboundMessageHandler(new TestVirtualActorProvider(factory));
        var result = await handler.HandleAsync(
            new RouteInboundMessageCommand(Ws, new InboundMessage
            {
                ChannelId = ChannelId.New(),
                SourceChannel = ChannelType.Slack,
                SenderId = "u1",
                SenderName = "alice",
                Content = "hi"
            }),
            TestContext.Current.CancellationToken);

        result.ShouldBe(outbound);
    }

    [Fact]
    public async Task GetSkillHandler_FoundSkill_ReturnsIt()
    {
        var factory = Substitute.For<IActorFactory>();
        var memory = Substitute.For<ISkillMemoryActor>();
        var skillId = SkillId.New();
        var skill = new SkillDocument
        {
            SkillId = skillId,
            Title = "t",
            Description = "d",
            Tags = [],
            Steps = [],
            ToolsUsed = [],
            CreatedByAgent = "agent-1"
        };
        factory.GetGrain<ISkillMemoryActor>("ws-1", null).Returns(memory);
        memory.GetSkillAsync(skillId).Returns(skill);

        var handler = new GetSkillHandler(new TestVirtualActorProvider(factory));
        var result = await handler.HandleAsync(
            new GetSkillQuery(Ws, skillId),
            TestContext.Current.CancellationToken);

        result.SkillId.ShouldBe(skillId);
    }

    [Fact]
    public async Task GetSkillHandler_MissingSkill_ThrowsKeyNotFoundException()
    {
        var factory = Substitute.For<IActorFactory>();
        var memory = Substitute.For<ISkillMemoryActor>();
        factory.GetGrain<ISkillMemoryActor>("ws-1", null).Returns(memory);
        memory.GetSkillAsync(Arg.Any<SkillId>()).Returns((SkillDocument?)null);

        var handler = new GetSkillHandler(new TestVirtualActorProvider(factory));

        await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.HandleAsync(new GetSkillQuery(Ws, SkillId.New()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetUserProfileHandler_ReturnsActorProfile()
    {
        var factory = Substitute.For<IActorFactory>();
        var user = Substitute.For<IUserModelActor>();
        var profile = new UserProfileState { UserId = "user-1", WorkspaceId = "ws-1" };
        factory.GetGrain<IUserModelActor>("ws-1/user-1", null).Returns(user);
        user.GetProfileAsync().Returns(profile);

        var handler = new GetUserProfileHandler(new TestVirtualActorProvider(factory));
        var result = await handler.HandleAsync(
            new GetUserProfileQuery(Ws, "user-1"),
            TestContext.Current.CancellationToken);

        result.UserId.ShouldBe("user-1");
    }

    [Fact]
    public async Task SearchSkillsHandler_ReturnsActorResults()
    {
        var factory = Substitute.For<IActorFactory>();
        var memory = Substitute.For<ISkillMemoryActor>();
        IReadOnlyList<SkillSearchResult> results =
        [
            new() { Skill = new SkillDocument { SkillId = SkillId.New(), Title = "t", Description = "d", Tags = [], Steps = [], ToolsUsed = [], CreatedByAgent = "a" }, RelevanceScore = 0.9 }
        ];
        factory.GetGrain<ISkillMemoryActor>("ws-1", null).Returns(memory);
        memory.SearchAsync("test", 5).Returns(results);

        var handler = new SearchSkillsHandler(new TestVirtualActorProvider(factory));
        var result = await handler.HandleAsync(
            new SearchSkillsQuery(Ws, "test", 5),
            TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result[0].RelevanceScore.ShouldBe(0.9);
    }
}
