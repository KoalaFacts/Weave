using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Tools.Builders;
using Weave.Tools.Grains;

namespace Weave.Agents.Pipeline;

public sealed class AgentChatPipeline(
    IGrainFactory grainFactory,
    IAgentChatClientFactory chatClientFactory,
    ILogger<AgentChatPipeline> logger) : IAgentChatPipeline
{
    private IChatClient? _chatClient;
    private string? _systemPrompt;

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
            Timestamp = DateTimeOffset.UtcNow
        };
        state.History.Add(userEntry);
        state.LastActive = userEntry.Timestamp;

        var prompt = await GetSystemPromptAsync(state);
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
            foreach (var conversationMessage in ChatMessageMapper.ToConversationMessages(responseMessage))
            {
                state.History.Add(conversationMessage);
                newMessages.Add(conversationMessage);
            }
        }

        state.LastActive = DateTimeOffset.UtcNow;

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
        var registry = grainFactory.GetGrain<IToolRegistryGrain>(state.WorkspaceId.ToString());
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
        var registry = grainFactory.GetGrain<IToolRegistryGrain>(state.WorkspaceId.ToString());
        var resolution = await registry.ResolveAsync(state.AgentName, toolName)
            ?? throw new InvalidOperationException($"Tool '{toolName}' is not available to agent '{state.AgentName}'.");

        var toolGrain = grainFactory.GetGrain<IToolGrain>(resolution.GrainKey);
        var invocation = ToolInvocationBuilder.FromInput(toolName, input);
        var result = await toolGrain.InvokeAsync(invocation, resolution.Token);
        return result.Success ? result.Output : $"Tool '{toolName}' failed: {result.Error}";
    }
}
