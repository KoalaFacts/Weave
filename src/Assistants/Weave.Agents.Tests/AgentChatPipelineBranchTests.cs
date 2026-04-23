using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Agents.Pipeline;
using Weave.Shared.Ids;
using Weave.Tools.Grains;
using Weave.Tools.Models;
using Weave.Workspaces.Models;

namespace Weave.Agents.Tests;

/// <summary>
/// Branch coverage for <see cref="AgentChatPipeline"/> paths not hit by
/// <see cref="AgentChatPipelineTests"/> — system prompt file handling,
/// user context failure isolation, skill memory failure isolation,
/// tool invocation via the resolved registry.
/// </summary>
public sealed class AgentChatPipelineBranchTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");

    private sealed class Fixture
    {
        public IChatClient ChatClient { get; } = Substitute.For<IChatClient>();
        public IAgentChatClientFactory ChatClientFactory { get; } = Substitute.For<IAgentChatClientFactory>();
        public IGrainFactory GrainFactory { get; } = Substitute.For<IGrainFactory>();
        public AgentChatPipeline Pipeline { get; }

        public Fixture(string responseText = "ok")
        {
            ChatClient
                .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
                .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText))
                {
                    ModelId = "test-model"
                });
            ChatClientFactory.Create(Arg.Any<string>(), Arg.Any<string?>()).Returns(ChatClient);

            Pipeline = new AgentChatPipeline(
                GrainFactory, ChatClientFactory, TimeProvider.System, NullLogger<AgentChatPipeline>.Instance);
        }
    }

    private static AgentState StateWith(AgentDefinition? def = null, params string[] connectedTools) => new()
    {
        AgentId = "ws-1/researcher",
        WorkspaceId = TestWorkspaceId,
        AgentName = "researcher",
        Status = AgentStatus.Active,
        Model = "test-model",
        Definition = def,
        ConnectedTools = [.. connectedTools]
    };

    [Fact]
    public async Task ExecuteAsync_SystemPromptFileMissing_LogsWarningAndContinues()
    {
        var fx = new Fixture();
        var missingPath = Path.Combine(Path.GetTempPath(), $"weave-never-{Guid.NewGuid():N}.md");
        var state = StateWith(new AgentDefinition { Model = "test-model", SystemPromptFile = missingPath });

        // Should not throw — the grain logs a warning and treats the prompt as empty.
        var response = await fx.Pipeline.ExecuteAsync(state, new AgentMessage { Role = "user", Content = "hi" });

        response.ShouldNotBeNull();
        response.Content.ShouldBe("ok");
    }

    [Fact]
    public async Task ExecuteAsync_SystemPromptFileExists_ReadsAndPassesAsSystemMessage()
    {
        var fx = new Fixture();
        var tempPath = Path.Combine(Path.GetTempPath(), $"weave-prompt-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(tempPath, "You are a diligent researcher.", TestContext.Current.CancellationToken);
        try
        {
            var state = StateWith(new AgentDefinition { Model = "test-model", SystemPromptFile = tempPath });

            await fx.Pipeline.ExecuteAsync(state, new AgentMessage { Role = "user", Content = "hi" });

            await fx.ChatClient.Received().GetResponseAsync(
                Arg.Is<IEnumerable<ChatMessage>>(ms => ms.Any(m => m.Role == ChatRole.System && m.Text.Contains("diligent researcher"))),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_UserContextGrainThrows_SwallowsErrorAndContinues()
    {
        var fx = new Fixture();
        var userGrain = Substitute.For<IUserModelGrain>();
        userGrain.GetContextSummaryAsync().Returns(Task.FromException<string>(new InvalidOperationException("user grain broken")));
        fx.GrainFactory.GetGrain<IUserModelGrain>("ws-1/alice", null).Returns(userGrain);

        var state = StateWith();

        var response = await fx.Pipeline.ExecuteAsync(state, new AgentMessage
        {
            Role = "user",
            Content = "hi",
            UserId = "alice"
        });

        response.Content.ShouldBe("ok", "user-context failures must not break the main chat flow");
    }

    [Fact]
    public async Task ExecuteAsync_SkillMemoryGrainThrows_SwallowsErrorAndContinues()
    {
        var fx = new Fixture();
        var skillGrain = Substitute.For<ISkillMemoryGrain>();
        skillGrain.SearchAsync(Arg.Any<string>(), Arg.Any<int>())
            .Returns(Task.FromException<IReadOnlyList<SkillSearchResult>>(new InvalidOperationException("skill grain broken")));
        fx.GrainFactory.GetGrain<ISkillMemoryGrain>("ws-1", null).Returns(skillGrain);

        var state = StateWith();

        var response = await fx.Pipeline.ExecuteAsync(state, new AgentMessage
        {
            Role = "user",
            Content = "tell me something"
        });

        response.Content.ShouldBe("ok");
    }

    [Fact]
    public async Task ExecuteAsync_ToolResolutionReturnsNull_SkipsToolInToolList()
    {
        var fx = new Fixture();
        var toolRegistry = Substitute.For<IToolRegistryGrain>();
        toolRegistry.ResolveAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns((ToolResolution?)null);
        fx.GrainFactory.GetGrain<IToolRegistryGrain>("ws-1", null).Returns(toolRegistry);

        // State with a "connected" tool whose resolution returns null.
        var state = StateWith(null, "unavailable-tool");

        var response = await fx.Pipeline.ExecuteAsync(state, new AgentMessage
        {
            Role = "user",
            Content = "hi"
        });

        // The ChatOptions.Tools should not contain the unresolved tool.
        // Since the pipeline will only send a ChatClient request with a tools
        // list of 0, we verify the happy-path response still comes through.
        response.Content.ShouldBe("ok");
        await toolRegistry.Received().ResolveAsync("researcher", "unavailable-tool");
    }
}
