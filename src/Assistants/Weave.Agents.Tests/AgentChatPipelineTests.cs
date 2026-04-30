using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Agents.Pipeline;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

public sealed class AgentChatPipelineTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");

    private static AgentState CreateActiveState() =>
        new()
        {
            AgentId = "ws-1/researcher",
            WorkspaceId = TestWorkspaceId,
            AgentName = "researcher",
            Status = AgentStatus.Active,
            Model = "claude-sonnet-4-20250514"
        };

    private static (AgentChatPipeline Pipeline, IChatClient ChatClient) CreatePipeline()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello back"))
            {
                ModelId = "claude-sonnet-4-20250514"
            });

        var chatClientFactory = Substitute.For<IAgentChatClientFactory>();
        chatClientFactory.Create(Arg.Any<string>(), Arg.Any<string?>()).Returns(chatClient);

        var actors = Substitute.For<IVirtualActorProvider>();
        var logger = NullLogger<AgentChatPipeline>.Instance;

        var pipeline = new AgentChatPipeline(actors, chatClientFactory, TimeProvider.System, logger);
        return (pipeline, chatClient);
    }

    [Fact]
    public async Task ExecuteAsync_AddsUserMessageToHistory()
    {
        var (pipeline, _) = CreatePipeline();
        var state = CreateActiveState();
        var message = new AgentMessage { Content = "Hello" };

        await pipeline.ExecuteAsync(state, message);

        state.History.ShouldContain(m => m.Role == "user" && m.Content == "Hello");
    }

    [Fact]
    public async Task ExecuteAsync_CallsChatClient()
    {
        var (pipeline, chatClient) = CreatePipeline();
        var state = CreateActiveState();
        var message = new AgentMessage { Content = "Hello" };

        await pipeline.ExecuteAsync(state, message);

        await chatClient.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsResponseContent()
    {
        var (pipeline, _) = CreatePipeline();
        var state = CreateActiveState();
        var message = new AgentMessage { Content = "Hello" };

        var response = await pipeline.ExecuteAsync(state, message);

        response.Content.ShouldBe("Hello back");
        response.Model.ShouldBe("claude-sonnet-4-20250514");
    }

    [Fact]
    public async Task ExecuteAsync_AddsResponseToHistory()
    {
        var (pipeline, _) = CreatePipeline();
        var state = CreateActiveState();
        var message = new AgentMessage { Content = "Hello" };

        await pipeline.ExecuteAsync(state, message);

        state.History.ShouldContain(m => m.Role == "assistant" && m.Content == "Hello back");
    }

    [Fact]
    public async Task ExecuteAsync_UpdatesLastActive()
    {
        var (pipeline, _) = CreatePipeline();
        var state = CreateActiveState();
        var before = DateTimeOffset.UtcNow;
        var message = new AgentMessage { Content = "Hello" };

        await pipeline.ExecuteAsync(state, message);

        state.LastActive.ShouldNotBeNull();
        state.LastActive!.Value.ShouldBeGreaterThanOrEqualTo(before);
    }

    [Fact]
    public void Initialize_CreatesChatClient()
    {
        var chatClientFactory = Substitute.For<IAgentChatClientFactory>();
        chatClientFactory.Create(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(Substitute.For<IChatClient>());
        var actors = Substitute.For<IVirtualActorProvider>();
        var pipeline = new AgentChatPipeline(actors, chatClientFactory, TimeProvider.System, NullLogger<AgentChatPipeline>.Instance);

        pipeline.Initialize("ws-1/researcher", "claude-sonnet-4-20250514");

        chatClientFactory.Received(1).Create("ws-1/researcher", "claude-sonnet-4-20250514");
    }

    [Fact]
    public async Task ExecuteAsync_AfterReset_RecreatesChatClient()
    {
        var (pipeline, chatClient) = CreatePipeline();
        var state = CreateActiveState();

        pipeline.Initialize("ws-1/researcher", "claude-sonnet-4-20250514");
        pipeline.Reset();
        await pipeline.ExecuteAsync(state, new AgentMessage { Content = "Hello" });

        await chatClient.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithConnectedTools_ResolvesFromRegistry()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK"))
            {
                ModelId = "claude-sonnet-4-20250514"
            });

        var chatClientFactory = Substitute.For<IAgentChatClientFactory>();
        chatClientFactory.Create(Arg.Any<string>(), Arg.Any<string?>()).Returns(chatClient);

        var registry = Substitute.For<IToolRegistryActor>();
        registry.ResolveAsync("researcher", "code-search")
            .Returns(Task.FromResult<ToolResolution?>(null));

        var actors = Substitute.For<IVirtualActorProvider>();
        actors.GetActor<IToolRegistryActor>(Arg.Any<VirtualActorId>()).Returns(registry);

        var pipeline = new AgentChatPipeline(actors, chatClientFactory, TimeProvider.System, NullLogger<AgentChatPipeline>.Instance);

        var state = CreateActiveState();
        state.ConnectedTools.Add("code-search");

        await pipeline.ExecuteAsync(state, new AgentMessage { Content = "Search for X" });

        await registry.Received(1).ResolveAsync("researcher", "code-search");
    }

    [Fact]
    public async Task ExecuteAsync_WithUserId_EnrichesPromptWithUserContext()
    {
        var chatClient = Substitute.For<IChatClient>();
        IEnumerable<ChatMessage>? capturedMessages = null;
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedMessages = callInfo.Arg<IEnumerable<ChatMessage>>().ToList();
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hi"))
                {
                    ModelId = "claude-sonnet-4-20250514"
                };
            });

        var chatClientFactory = Substitute.For<IAgentChatClientFactory>();
        chatClientFactory.Create(Arg.Any<string>(), Arg.Any<string?>()).Returns(chatClient);

        var userModelActor = Substitute.For<IUserModelActor>();
        userModelActor.GetContextSummaryAsync()
            .Returns(Task.FromResult("User preferences: lang=csharp. Interactions: 5 total."));

        var skillActor = Substitute.For<ISkillMemoryActor>();
        skillActor.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<SkillSearchOptions>())
            .Returns(Task.FromResult<IReadOnlyList<SkillSearchResult>>([]));

        var actors = Substitute.For<IVirtualActorProvider>();
        actors.GetActor<IUserModelActor>(Arg.Any<VirtualActorId>()).Returns(userModelActor);
        actors.GetActor<ISkillMemoryActor>(Arg.Any<VirtualActorId>()).Returns(skillActor);

        var pipeline = new AgentChatPipeline(actors, chatClientFactory, TimeProvider.System, NullLogger<AgentChatPipeline>.Instance);
        var state = CreateActiveState();

        await pipeline.ExecuteAsync(state, new AgentMessage { Content = "Hello", UserId = "user-42" });

        capturedMessages.ShouldNotBeNull();
        var systemMsg = capturedMessages.FirstOrDefault(m => m.Role == ChatRole.System);
        systemMsg.ShouldNotBeNull();
        systemMsg.Text.ShouldContain("User preferences: lang=csharp");
    }

    [Fact]
    public async Task ExecuteAsync_EnrichesPromptWithMatchingSkills()
    {
        var chatClient = Substitute.For<IChatClient>();
        IEnumerable<ChatMessage>? capturedMessages = null;
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedMessages = callInfo.Arg<IEnumerable<ChatMessage>>().ToList();
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done"))
                {
                    ModelId = "claude-sonnet-4-20250514"
                };
            });

        var chatClientFactory = Substitute.For<IAgentChatClientFactory>();
        chatClientFactory.Create(Arg.Any<string>(), Arg.Any<string?>()).Returns(chatClient);

        var skill = new SkillDocument
        {
            SkillId = SkillId.New(),
            Title = "Deploy to K8s",
            Description = "Steps to deploy a service to Kubernetes",
            Tags = ["deploy", "k8s"],
            Steps =
            [
                new SkillStep { Order = 0, Action = "Build image" },
                new SkillStep { Order = 1, Action = "Push to registry" }
            ],
            ToolsUsed = ["docker", "kubectl"],
            CreatedByAgent = "deployer"
        };

        var skillActor = Substitute.For<ISkillMemoryActor>();
        skillActor.SearchAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Is<SkillSearchOptions>(options => options.MinSuccessRate == 0.5 && options.PreferRecent))
            .Returns(Task.FromResult<IReadOnlyList<SkillSearchResult>>([
                new SkillSearchResult { Skill = skill, RelevanceScore = 5.0 }
            ]));

        var actors = Substitute.For<IVirtualActorProvider>();
        actors.GetActor<ISkillMemoryActor>(Arg.Any<VirtualActorId>()).Returns(skillActor);

        var pipeline = new AgentChatPipeline(actors, chatClientFactory, TimeProvider.System, NullLogger<AgentChatPipeline>.Instance);
        var state = CreateActiveState();

        await pipeline.ExecuteAsync(state, new AgentMessage { Content = "deploy to k8s" });

        capturedMessages.ShouldNotBeNull();
        var systemMsg = capturedMessages.FirstOrDefault(m => m.Role == ChatRole.System);
        systemMsg.ShouldNotBeNull();
        systemMsg.Text.ShouldContain("Deploy to K8s");
        systemMsg.Text.ShouldContain("Build image");
    }

    [Fact]
    public async Task ExecuteAsync_WithMatchingSkills_RecordsSuccessfulSkillUsage()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done"))
            {
                ModelId = "claude-sonnet-4-20250514"
            });

        var chatClientFactory = Substitute.For<IAgentChatClientFactory>();
        chatClientFactory.Create(Arg.Any<string>(), Arg.Any<string?>()).Returns(chatClient);

        var skill = new SkillDocument
        {
            SkillId = SkillId.From("deploy-skill"),
            Title = "Deploy to K8s",
            Description = "Steps to deploy a service to Kubernetes",
            Tags = ["deploy", "k8s"],
            Steps = [new SkillStep { Order = 0, Action = "Build image" }],
            ToolsUsed = ["docker"],
            CreatedByAgent = "deployer"
        };

        var skillActor = Substitute.For<ISkillMemoryActor>();
        skillActor.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<SkillSearchOptions>())
            .Returns(Task.FromResult<IReadOnlyList<SkillSearchResult>>([
                new SkillSearchResult { Skill = skill, RelevanceScore = 5.0 }
            ]));
        skillActor.RecordUsageAsync(skill.SkillId, success: true).Returns(Task.CompletedTask);

        var actors = Substitute.For<IVirtualActorProvider>();
        actors.GetActor<ISkillMemoryActor>(Arg.Any<VirtualActorId>()).Returns(skillActor);

        var pipeline = new AgentChatPipeline(actors, chatClientFactory, TimeProvider.System, NullLogger<AgentChatPipeline>.Instance);

        await pipeline.ExecuteAsync(CreateActiveState(), new AgentMessage { Content = "deploy to k8s" });

        await skillActor.Received(1).RecordUsageAsync(skill.SkillId, success: true);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutUserId_SkipsUserContext()
    {
        var (pipeline, chatClient) = CreatePipeline();
        var state = CreateActiveState();

        await pipeline.ExecuteAsync(state, new AgentMessage { Content = "Hello" });

        // Should not throw and should complete normally without user context
        await chatClient.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>());
    }
}
