using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Weave.Agents.Channels;
using Weave.Agents.Chat;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Security.Tokens;
using Weave.Shared.Capabilities;
using Weave.Shared.Ids;
using Weave.Tools.Builders;
using Weave.Tools.Marketplace;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;

namespace Weave.Agents.Pipeline;

public sealed class AgentChatPipeline(
    IVirtualActorProvider actors,
    IAgentChatClientFactory chatClientFactory,
    ICapabilityTokenService tokenService,
    TimeProvider timeProvider,
    ILogger<AgentChatPipeline> logger) : IAgentChatPipeline
{
    private IChatClient? _chatClient;
    private string? _systemPrompt;
    private readonly SkillMemoryPromptEnricher _skillMemory = new(actors, tokenService);
    private readonly EpisodicMemoryPromptEnricher _episodicMemory = new(actors, logger);

    public async Task InitializeAsync(string agentId, AgentDefinition? definition, CancellationToken ct = default)
    {
        _chatClient = await chatClientFactory.CreateAsync(agentId, definition, ct);
    }

    public void Reset()
    {
        _chatClient = null;
        _systemPrompt = null;
    }

    public async Task<AgentChatResponse> ExecuteAsync(AgentState state, AgentMessage message)
    {
        var request = await BuildRequestAsync(state, message);
        var response = await _chatClient!.GetResponseAsync(request.Messages, request.Options, CancellationToken.None);
        return await FinishAsync(state, response, request.SkillIds, request.EpisodeIds);
    }

    public async IAsyncEnumerable<AgentChatStreamingFrame> ExecuteStreamingAsync(
        AgentState state,
        AgentMessage message,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = await BuildRequestAsync(state, message);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in _chatClient!.GetStreamingResponseAsync(request.Messages, request.Options, ct).ConfigureAwait(false))
        {
            updates.Add(update);
            foreach (var content in update.Contents)
            {
                if (content is TextContent { Text: { Length: > 0 } text })
                    yield return new AgentChatTextFrame(text);
            }
        }

        var response = updates.ToChatResponse();
        var final = await FinishAsync(state, response, request.SkillIds, request.EpisodeIds);
        yield return new AgentChatCompleteFrame(final);
    }

    private async Task<ChatRequest> BuildRequestAsync(AgentState state, AgentMessage message)
    {
        _chatClient ??= await chatClientFactory.CreateAsync(state.AgentId, state.Definition);

        var userEntry = new ConversationMessage
        {
            Role = string.IsNullOrWhiteSpace(message.Role) ? "user" : message.Role,
            Content = message.Content,
            Timestamp = timeProvider.GetUtcNow()
        };
        state.History.Add(userEntry);
        state.LastActive = userEntry.Timestamp;

        var prompt = await GetSystemPromptAsync(state);
        prompt = await EnrichWithUserContextAsync(state, message, prompt);
        var skillMemory = await _skillMemory.EnrichAsync(state, message.Content, prompt);
        prompt = skillMemory.Prompt;
        var episodicMemory = await _episodicMemory.EnrichAsync(state.WorkspaceId, state.AgentName, message.Content, prompt);
        prompt = episodicMemory.Prompt;

        var chatMessages = new List<ChatMessage>(state.History.Count + 1);
        if (!string.IsNullOrWhiteSpace(prompt))
            chatMessages.Add(new ChatMessage(ChatRole.System, prompt));

        foreach (var historyMessage in state.History)
            chatMessages.Add(ChatMessageMapper.ToChatMessage(historyMessage));

        var tools = await BuildToolsAsync(state);
        var options = new ChatOptions
        {
            ModelId = state.Model,
            ConversationId = state.ConversationId,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["agentId"] = state.AgentId
            }
        };

        if (tools.Count > 0)
            options.Tools = tools;

        return new ChatRequest(chatMessages, options, skillMemory.SkillIds, episodicMemory.EpisodeIds);
    }

    private async Task<AgentChatResponse> FinishAsync(
        AgentState state,
        ChatResponse response,
        IReadOnlyList<SkillId> skillIds,
        IReadOnlyList<EpisodeId> episodeIds)
    {
        state.ConversationId = response.ConversationId ?? state.ConversationId;

        var newMessages = new List<ConversationMessage>();
        foreach (var responseMessage in response.Messages)
        {
            foreach (var conversationMessage in ChatMessageMapper.ToConversationMessages(responseMessage, timeProvider))
            {
                state.History.Add(conversationMessage);
                newMessages.Add(conversationMessage);
            }
        }

        state.LastActive = timeProvider.GetUtcNow();
        await _skillMemory.RecordSuccessfulUsageAsync(state, skillIds);
        await _episodicMemory.RecordRecallAsync(state.WorkspaceId, state.AgentName, episodeIds);

        return new AgentChatResponse
        {
            Content = response.Text,
            ConversationId = state.ConversationId ?? string.Empty,
            Messages = newMessages,
            UsedTools = response.Messages.Any(static m => m.Contents.Any(static c => c is FunctionCallContent or FunctionResultContent)),
            Model = response.ModelId ?? state.Model
        };
    }

    private sealed record ChatRequest(
        IReadOnlyList<ChatMessage> Messages,
        ChatOptions Options,
        IReadOnlyList<SkillId> SkillIds,
        IReadOnlyList<EpisodeId> EpisodeIds);

    private async Task<string?> GetSystemPromptAsync(AgentState state)
    {
        if (_systemPrompt is not null || state.Definition?.SystemPromptFile is null)
            return _systemPrompt;

        var promptPath = state.Definition.SystemPromptFile;
        if (!File.Exists(promptPath))
        {
            logger.LogWarning("System prompt file '{PromptPath}' was not found for agent {AgentName}", promptPath, state.AgentName);
            _systemPrompt = string.Empty;
            return _systemPrompt;
        }

        _systemPrompt = await File.ReadAllTextAsync(promptPath);
        return _systemPrompt;
    }

    private async Task<List<AITool>> BuildToolsAsync(AgentState state)
    {
        var registry = actors.GetActor<IToolRegistryActor>(VirtualActorId.From(state.WorkspaceId.ToString()));
        var tools = new List<AITool>(state.ConnectedTools.Count);

        foreach (var toolName in state.ConnectedTools)
        {
            var resolution = await registry.ResolveAsync(state.AgentName, toolName);
            if (resolution is null)
                continue;

            Func<string, Task<string>> toolDelegate = input => InvokeToolAsync(state, toolName, input);
            var function = AIFunctionFactory.Create(
                toolDelegate,
                new AIFunctionFactoryOptions
                {
                    Name = toolName,
                    Description = ToolInvocationBuilder.DescribeSchema(resolution.Schema)
                });
            tools.Add(function);
        }

        return tools;
    }

    private async Task<string> InvokeToolAsync(AgentState state, string toolName, string input)
    {
        var registry = actors.GetActor<IToolRegistryActor>(VirtualActorId.From(state.WorkspaceId.ToString()));
        var resolution = await registry.ResolveAsync(state.AgentName, toolName)
            ?? throw new InvalidOperationException($"Tool '{toolName}' is not available to agent '{state.AgentName}'.");

        var toolActor = actors.GetActor<IToolActor>(VirtualActorId.From(resolution.ActorKey));
        var invocation = ToolInvocationBuilder.FromInput(toolName, input);
        var result = await toolActor.InvokeAsync(invocation, resolution.Token);
        return result.Success ? result.Output : $"Tool '{toolName}' failed: {result.Error}";
    }

    private async Task<string?> EnrichWithUserContextAsync(AgentState state, AgentMessage message, string? prompt)
    {
        if (string.IsNullOrWhiteSpace(message.UserId))
            return prompt;

        var grant = $"user:read:{message.UserId}";
        if (state.Definition?.Capabilities is not { } capabilities || !CapabilityGrantMatcher.HasGrant(capabilities, grant))
            return prompt;

        var userActor = actors.GetActor<IUserModelActor>(VirtualActorId.Combine(state.WorkspaceId, message.UserId));
        using var source = tokenService.MintLinked(new CapabilityTokenRequest
        {
            WorkspaceId = state.WorkspaceId.ToString(),
            IssuedTo = $"{state.WorkspaceId}/{state.AgentName}",
            Grants = [grant],
            Lifetime = TimeSpan.FromMinutes(1)
        }, CancellationToken.None);
        var summary = await userActor.GetContextSummaryAsync(source.Token);
        if (string.IsNullOrWhiteSpace(summary))
            return prompt;

        return string.IsNullOrWhiteSpace(prompt)
            ? $"[User context]\n{summary}"
            : $"{prompt}\n\n[User context]\n{summary}";
    }

}
