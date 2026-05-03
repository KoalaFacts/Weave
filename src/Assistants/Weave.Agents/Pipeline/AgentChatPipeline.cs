using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Security.Tokens;
using Weave.Tools.Actors;
using Weave.Tools.Builders;

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
    private readonly SkillMemoryPromptEnricher _skillMemory = new(actors, tokenService, logger);
    private readonly EpisodicMemoryPromptEnricher _episodicMemory = new(actors, logger);

    public void Initialize(string agentId, string? model)
    {
        _chatClient = chatClientFactory.Create(agentId, model);
    }

    public void Reset()
    {
        _chatClient = null;
        _systemPrompt = null;
    }

    public async Task<AgentChatResponse> ExecuteAsync(AgentState state, AgentMessage message)
    {
        _chatClient ??= chatClientFactory.Create(state.AgentId, state.Model);

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

        var response = await _chatClient.GetResponseAsync(chatMessages, options, CancellationToken.None);
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
        await _skillMemory.RecordSuccessfulUsageAsync(state, skillMemory.SkillIds);
        await _episodicMemory.RecordRecallAsync(state.WorkspaceId, state.AgentName, episodicMemory.EpisodeIds);

        return new AgentChatResponse
        {
            Content = response.Text,
            ConversationId = state.ConversationId ?? string.Empty,
            Messages = newMessages,
            UsedTools = response.Messages.Any(static m => m.Contents.Any(static c => c is FunctionCallContent or FunctionResultContent)),
            Model = response.ModelId ?? state.Model
        };
    }

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
        if (state.Definition?.Capabilities is not { } capabilities || !CapabilityToken.HasGrant(capabilities, grant))
            return prompt;

        try
        {
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
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to retrieve user context for {UserId}", message.UserId);
            return prompt;
        }
    }

}
