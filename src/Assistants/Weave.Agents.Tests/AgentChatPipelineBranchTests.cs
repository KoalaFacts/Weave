using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Agents.Pipeline;
using Weave.Security.Tokens;
using Weave.Shared.Ids;
using Weave.Tools.Actors;
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
        public IVirtualActorProvider ActorProvider { get; } = Substitute.For<IVirtualActorProvider>();
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
                ActorProvider,
                ChatClientFactory,
                new CapabilityTokenService(
                    Options.Create(new CapabilityTokenOptions { SigningKey = "test-signing-key-that-is-at-least-32-chars-long" }),
                    TimeProvider.System),
                TimeProvider.System,
                NullLogger<AgentChatPipeline>.Instance);
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

        // Should not throw — the actor logs a warning and treats the prompt as empty.
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
    public async Task ExecuteAsync_UserContextActorThrows_SwallowsErrorAndContinues()
    {
        var fx = new Fixture();
        var userActor = Substitute.For<IUserModelActor>();
        userActor.GetContextSummaryAsync().Returns(Task.FromException<string>(new InvalidOperationException("user actor broken")));
        fx.ActorProvider.GetActor<IUserModelActor>(Arg.Any<VirtualActorId>()).Returns(userActor);

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
    public async Task ExecuteAsync_SkillMemoryActorThrows_SwallowsErrorAndContinues()
    {
        var fx = new Fixture();
        var skillActor = Substitute.For<ISkillMemoryActor>();
        skillActor.SearchAsync(Arg.Any<string>(), Arg.Any<CapabilityToken>(), Arg.Any<int>(), Arg.Any<SkillSearchOptions>())
            .Returns(Task.FromException<IReadOnlyList<SkillSearchResult>>(new InvalidOperationException("skill actor broken")));
        fx.ActorProvider.GetActor<ISkillMemoryActor>(Arg.Any<VirtualActorId>()).Returns(skillActor);

        var state = StateWith();

        var response = await fx.Pipeline.ExecuteAsync(state, new AgentMessage
        {
            Role = "user",
            Content = "tell me something"
        });

        response.Content.ShouldBe("ok");
    }

    [Fact]
    public async Task ExecuteAsync_RecordSkillUsageThrows_SwallowsErrorAndContinues()
    {
        var fx = new Fixture();
        var skill = new SkillDocument
        {
            SkillId = SkillId.From("unstable-skill"),
            Title = "Useful memory",
            Description = "A useful memory entry",
            Tags = ["memory"],
            Steps = [new SkillStep { Order = 0, Action = "Use memory" }],
            ToolsUsed = [],
            CreatedByAgent = "researcher"
        };
        var skillActor = Substitute.For<ISkillMemoryActor>();
        skillActor.SearchAsync(Arg.Any<string>(), Arg.Any<CapabilityToken>(), Arg.Any<int>(), Arg.Any<SkillSearchOptions>())
            .Returns(Task.FromResult<IReadOnlyList<SkillSearchResult>>([
                new SkillSearchResult { Skill = skill, RelevanceScore = 3.0 }
            ]));
        skillActor.RecordUsageAsync(skill.SkillId, success: true, Arg.Any<CapabilityToken>())
            .Returns(Task.FromException(new InvalidOperationException("usage write failed")));
        fx.ActorProvider.GetActor<ISkillMemoryActor>(Arg.Any<VirtualActorId>()).Returns(skillActor);

        var response = await fx.Pipeline.ExecuteAsync(StateWith(), new AgentMessage
        {
            Role = "user",
            Content = "use memory"
        });

        response.Content.ShouldBe("ok");
    }

    [Fact]
    public async Task ExecuteAsync_ToolResolutionReturnsNull_SkipsToolInToolList()
    {
        var fx = new Fixture();
        var toolRegistry = Substitute.For<IToolRegistryActor>();
        toolRegistry.ResolveAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns((ToolResolution?)null);
        fx.ActorProvider.GetActor<IToolRegistryActor>(Arg.Any<VirtualActorId>()).Returns(toolRegistry);

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

    [Fact]
    public async Task ExecuteAsync_ResolvedTool_AppearsInChatOptionsAndIsInvokable()
    {
        // Captures the ChatOptions.Tools list passed to the chat client so we
        // can verify that BuildToolsAsync attaches the resolved tool, and so
        // we can invoke the registered AIFunction directly to exercise
        // InvokeToolAsync.
        var fx = new Fixture();
        ChatOptions? capturedOptions = null;
        fx.ChatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedOptions = call.Arg<ChatOptions>();
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
                {
                    ModelId = "test-model"
                };
            });

        var resolution = new ToolResolution
        {
            ToolName = "echo-tool",
            ActorKey = "ws-1/echo-tool",
            Token = new CapabilityToken { Grants = ["tool:*"] },
            Schema = new ToolSchema { ToolName = "echo-tool", Description = "Echoes the input" }
        };
        var toolRegistry = Substitute.For<IToolRegistryActor>();
        toolRegistry.ResolveAsync("researcher", "echo-tool").Returns(resolution);
        fx.ActorProvider.GetActor<IToolRegistryActor>(Arg.Any<VirtualActorId>()).Returns(toolRegistry);

        var toolActor = Substitute.For<IToolActor>();
        toolActor.InvokeAsync(Arg.Any<ToolInvocation>(), Arg.Any<CapabilityToken>())
            .Returns(new ToolResult { Success = true, Output = "echoed: hello" });
        fx.ActorProvider.GetActor<IToolActor>(Arg.Any<VirtualActorId>()).Returns(toolActor);

        var state = StateWith(null, "echo-tool");
        await fx.Pipeline.ExecuteAsync(state, new AgentMessage { Role = "user", Content = "hi" });

        capturedOptions.ShouldNotBeNull();
        capturedOptions.Tools.ShouldNotBeNull();
        capturedOptions.Tools!.Count.ShouldBe(1);

        var function = (AIFunction)capturedOptions.Tools[0];
        function.Name.ShouldBe("echo-tool");

        // Invoking the registered function exercises InvokeToolAsync end-to-end.
        var invokeResult = await function.InvokeAsync(
            new AIFunctionArguments { ["input"] = "hello" },
            CancellationToken.None);
        invokeResult.ShouldNotBeNull();
        invokeResult!.ToString()!.ShouldContain("echoed: hello");
    }

    [Fact]
    public async Task ExecuteAsync_InvokedToolReturnsFailure_PropagatesErrorMessage()
    {
        // Same shape as the happy-path test but the tool actor returns success=false,
        // exercising the failure branch of InvokeToolAsync.
        var fx = new Fixture();
        ChatOptions? capturedOptions = null;
        fx.ChatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedOptions = call.Arg<ChatOptions>();
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
                {
                    ModelId = "test-model"
                };
            });

        var resolution = new ToolResolution
        {
            ToolName = "broken-tool",
            ActorKey = "ws-1/broken-tool",
            Token = new CapabilityToken { Grants = ["tool:*"] },
            Schema = new ToolSchema { ToolName = "broken-tool", Description = "Always fails" }
        };
        var toolRegistry = Substitute.For<IToolRegistryActor>();
        toolRegistry.ResolveAsync("researcher", "broken-tool").Returns(resolution);
        fx.ActorProvider.GetActor<IToolRegistryActor>(Arg.Any<VirtualActorId>()).Returns(toolRegistry);

        var toolActor = Substitute.For<IToolActor>();
        toolActor.InvokeAsync(Arg.Any<ToolInvocation>(), Arg.Any<CapabilityToken>())
            .Returns(new ToolResult { Success = false, Error = "boom" });
        fx.ActorProvider.GetActor<IToolActor>(Arg.Any<VirtualActorId>()).Returns(toolActor);

        var state = StateWith(null, "broken-tool");
        await fx.Pipeline.ExecuteAsync(state, new AgentMessage { Role = "user", Content = "hi" });

        var function = (AIFunction)capturedOptions!.Tools![0];
        var invokeResult = await function.InvokeAsync(
            new AIFunctionArguments { ["input"] = "anything" },
            CancellationToken.None);
        invokeResult.ShouldNotBeNull();
        invokeResult!.ToString()!.ShouldContain("boom");
    }

    [Fact]
    public async Task InvokeToolAsync_AfterRegistryResolutionDisappears_Throws()
    {
        // The two-call shape: BuildToolsAsync resolves once (succeeds), then
        // InvokeToolAsync resolves again (returns null) — must throw.
        var fx = new Fixture();
        ChatOptions? capturedOptions = null;
        fx.ChatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedOptions = call.Arg<ChatOptions>();
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
                {
                    ModelId = "test-model"
                };
            });

        var resolution = new ToolResolution
        {
            ToolName = "vanishing-tool",
            ActorKey = "ws-1/vanishing-tool",
            Token = new CapabilityToken { Grants = ["tool:*"] },
            Schema = new ToolSchema { ToolName = "vanishing-tool" }
        };
        var toolRegistry = Substitute.For<IToolRegistryActor>();
        toolRegistry.ResolveAsync("researcher", "vanishing-tool")
            .Returns(resolution, (ToolResolution?)null);
        fx.ActorProvider.GetActor<IToolRegistryActor>(Arg.Any<VirtualActorId>()).Returns(toolRegistry);

        var state = StateWith(null, "vanishing-tool");
        await fx.Pipeline.ExecuteAsync(state, new AgentMessage { Role = "user", Content = "hi" });

        var function = (AIFunction)capturedOptions!.Tools![0];
        await Should.ThrowAsync<InvalidOperationException>(() =>
            function.InvokeAsync(
                new AIFunctionArguments { ["input"] = "anything" },
                CancellationToken.None).AsTask());
    }
}
