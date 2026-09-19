using Weave.Agents.Channels;
using Weave.Agents.Chat;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Security.Tokens;
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
    private static readonly CapabilityToken StubToken = new() { Grants = ["*"] };

    [Fact]
    public async Task SendAgentMessageHandler_DelegatesToAgentActor()
    {
        var factory = Substitute.For<IVirtualActorProvider>();
        var agent = Substitute.For<IAgentActor>();
        var expected = new AgentChatResponse { Content = "hi back", ConversationId = "c1", UsedTools = false };
        factory.GetActor<IAgentActor>(Arg.Any<VirtualActorId>()).Returns(agent);
        agent.SendAsync(Arg.Any<AgentMessage>()).Returns(expected);

        var handler = new SendAgentMessageHandler(factory);
        var response = await handler.HandleAsync(
            new SendAgentMessageCommand(Ws, "agent-a", new AgentMessage { Role = "user", Content = "hi" }),
            TestContext.Current.CancellationToken);

        response.ShouldBe(expected);
    }

    [Fact]
    public async Task SetUserPreferenceHandler_CallsActorSetPreferenceAndReturnsTrue()
    {
        var factory = Substitute.For<IVirtualActorProvider>();
        var user = Substitute.For<IUserModelActor>();
        factory.GetActor<IUserModelActor>(Arg.Any<VirtualActorId>()).Returns(user);

        var handler = new SetUserPreferenceHandler(factory);
        var result = await handler.HandleAsync(
            new SetUserPreferenceCommand(Ws, "user-1", "theme", "dark", StubToken),
            TestContext.Current.CancellationToken);

        result.ShouldBeTrue();
        await user.Received(1).SetPreferenceAsync("theme", "dark", StubToken);
    }

    [Fact]
    public async Task StoreSkillHandler_DelegatesToSkillMemoryActor()
    {
        var factory = Substitute.For<IVirtualActorProvider>();
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
        factory.GetActor<ISkillMemoryActor>(Arg.Any<VirtualActorId>()).Returns(memory);
        memory.StoreSkillAsync(skill, Arg.Any<CapabilityToken>()).Returns(skill);

        var handler = new StoreSkillHandler(factory);
        var stored = await handler.HandleAsync(
            new StoreSkillCommand(Ws, skill, StubToken),
            TestContext.Current.CancellationToken);

        stored.SkillId.ShouldBe(skill.SkillId);
    }

    [Fact]
    public async Task RegisterChannelHandler_CallsActorRegisterChannelAndReturnsTrue()
    {
        var factory = Substitute.For<IVirtualActorProvider>();
        var gateway = Substitute.For<IChannelGatewayActor>();
        factory.GetActor<IChannelGatewayActor>(Arg.Any<VirtualActorId>()).Returns(gateway);
        var config = new ChannelConfig
        {
            ChannelId = ChannelId.New(),
            Type = ChannelType.Slack,
            Name = "slack-main"
        };

        var handler = new RegisterChannelHandler(factory);
        var result = await handler.HandleAsync(
            new RegisterChannelCommand(Ws, config),
            TestContext.Current.CancellationToken);

        result.ShouldBeTrue();
        await gateway.Received(1).RegisterChannelAsync(config);
    }

    [Fact]
    public async Task RouteInboundMessageHandler_ReturnsOutboundMessageFromActor()
    {
        var factory = Substitute.For<IVirtualActorProvider>();
        var gateway = Substitute.For<IChannelGatewayActor>();
        var outbound = new OutboundMessage { ChannelId = ChannelId.New(), Content = "response" };
        factory.GetActor<IChannelGatewayActor>(Arg.Any<VirtualActorId>()).Returns(gateway);
        gateway.RouteInboundAsync(Arg.Any<InboundMessage>(), Arg.Any<CapabilityToken>()).Returns(outbound);

        var handler = new RouteInboundMessageHandler(factory);
        var result = await handler.HandleAsync(
            new RouteInboundMessageCommand(Ws, new InboundMessage
            {
                ChannelId = ChannelId.New(),
                SourceChannel = ChannelType.Slack,
                SenderId = "u1",
                SenderName = "alice",
                Content = "hi"
            }, StubToken),
            TestContext.Current.CancellationToken);

        result.ShouldBe(outbound);
    }

    [Fact]
    public async Task GetSkillHandler_FoundSkill_ReturnsIt()
    {
        var factory = Substitute.For<IVirtualActorProvider>();
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
        factory.GetActor<ISkillMemoryActor>(Arg.Any<VirtualActorId>()).Returns(memory);
        memory.GetSkillAsync(skillId, Arg.Any<CapabilityToken>()).Returns(skill);

        var handler = new GetSkillHandler(factory);
        var result = await handler.HandleAsync(
            new GetSkillQuery(Ws, skillId, StubToken),
            TestContext.Current.CancellationToken);

        result.SkillId.ShouldBe(skillId);
    }

    [Fact]
    public async Task GetSkillHandler_MissingSkill_ThrowsKeyNotFoundException()
    {
        var factory = Substitute.For<IVirtualActorProvider>();
        var memory = Substitute.For<ISkillMemoryActor>();
        factory.GetActor<ISkillMemoryActor>(Arg.Any<VirtualActorId>()).Returns(memory);
        memory.GetSkillAsync(Arg.Any<SkillId>(), Arg.Any<CapabilityToken>()).Returns((SkillDocument?)null);

        var handler = new GetSkillHandler(factory);

        await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.HandleAsync(new GetSkillQuery(Ws, SkillId.New(), StubToken), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetUserProfileHandler_ReturnsActorProfile()
    {
        var factory = Substitute.For<IVirtualActorProvider>();
        var user = Substitute.For<IUserModelActor>();
        var profile = new UserProfileState { UserId = "user-1", WorkspaceId = "ws-1" };
        factory.GetActor<IUserModelActor>(Arg.Any<VirtualActorId>()).Returns(user);
        user.GetProfileAsync(Arg.Any<CapabilityToken>()).Returns(profile);

        var handler = new GetUserProfileHandler(factory);
        var result = await handler.HandleAsync(
            new GetUserProfileQuery(Ws, "user-1", StubToken),
            TestContext.Current.CancellationToken);

        result.UserId.ShouldBe("user-1");
    }

    [Fact]
    public async Task SearchSkillsHandler_ReturnsActorResults()
    {
        var factory = Substitute.For<IVirtualActorProvider>();
        var memory = Substitute.For<ISkillMemoryActor>();
        IReadOnlyList<SkillSearchResult> results =
        [
            new() { Skill = new SkillDocument { SkillId = SkillId.New(), Title = "t", Description = "d", Tags = [], Steps = [], ToolsUsed = [], CreatedByAgent = "a" }, RelevanceScore = 0.9 }
        ];
        factory.GetActor<ISkillMemoryActor>(Arg.Any<VirtualActorId>()).Returns(memory);
        memory.SearchAsync(
                "test",
                Arg.Any<CapabilityToken>(),
                5,
                Arg.Is<SkillSearchOptions>(options => options.MinSuccessRate == 0.9 && options.PreferRecent))
            .Returns(results);

        var handler = new SearchSkillsHandler(factory);
        var result = await handler.HandleAsync(
            new SearchSkillsQuery(Ws, "test", StubToken, 5, new SkillSearchOptions { MinSuccessRate = 0.9, PreferRecent = true }),
            TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result[0].RelevanceScore.ShouldBe(0.9);
    }
}
