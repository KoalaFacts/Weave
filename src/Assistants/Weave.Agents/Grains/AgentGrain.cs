using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Weave.Agents.Events;
using Weave.Agents.Models;
using Weave.Agents.Pipeline;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Tools.Builders;
using Weave.Tools.Grains;
using Weave.Tools.Models;
using Weave.Workspaces.Models;

namespace Weave.Agents.Grains;

public sealed class AgentGrain(
    IGrainFactory grainFactory,
    IAgentChatClientFactory chatClientFactory,
    ILifecycleManager lifecycleManager,
    IEventBus eventBus,
    ILogger<AgentGrain> logger,
    [PersistentState("agent", "Default")] IPersistentState<AgentState> persistentState) : Grain, IAgentGrain
{
    private IChatClient? _chatClient;
    private string? _systemPrompt;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await persistentState.ReadStateAsync(cancellationToken);

        var key = TryGetPrimaryKeyString();
        if (string.IsNullOrWhiteSpace(persistentState.State.AgentId))
        {
            ApplyIdentity(key, persistentState.State.WorkspaceId);
            await persistentState.WriteStateAsync(cancellationToken);
        }

        if (persistentState.State.Definition is not null)
            _chatClient = chatClientFactory.Create(persistentState.State.AgentId, persistentState.State.Model);
    }

    public async Task<AgentState> ActivateAgentAsync(WorkspaceId workspaceId, AgentDefinition definition)
    {
        if (persistentState.State.Status is AgentStatus.Active or AgentStatus.Busy)
            return persistentState.State;

        EnsureIdentity(workspaceId);

        persistentState.State.Status = AgentStatus.Activating;
        persistentState.State.Model = definition.Model;
        persistentState.State.MaxConcurrentTasks = definition.MaxConcurrentTasks;
        persistentState.State.Definition = definition;

        var context = new LifecycleContext
        {
            WorkspaceId = workspaceId,
            AgentName = persistentState.State.AgentName,
            Phase = LifecyclePhase.AgentActivating
        };

        try
        {
            await lifecycleManager.RunHooksAsync(LifecyclePhase.AgentActivating, context, CancellationToken.None);

            _chatClient = chatClientFactory.Create(persistentState.State.AgentId, definition.Model);
            _systemPrompt = null;

            persistentState.State.Status = AgentStatus.Active;
            persistentState.State.ActivatedAt = DateTimeOffset.UtcNow;
            persistentState.State.DeactivatedAt = null;
            persistentState.State.ErrorMessage = null;
            persistentState.State.LastActive = persistentState.State.ActivatedAt;

            await persistentState.WriteStateAsync();

            await lifecycleManager.RunHooksAsync(
                LifecyclePhase.AgentActivated,
                context with { Phase = LifecyclePhase.AgentActivated },
                CancellationToken.None);

            await eventBus.PublishAsync(new AgentActivatedEvent
            {
                SourceId = persistentState.State.AgentId,
                AgentName = persistentState.State.AgentName,
                WorkspaceId = workspaceId,
                Model = definition.Model,
                Tools = definition.Tools
            }, CancellationToken.None);

            logger.LogInformation(
                "Agent {AgentName} activated in workspace {WorkspaceId}",
                persistentState.State.AgentName,
                workspaceId);
        }
        catch (Exception ex)
        {
            persistentState.State.Status = AgentStatus.Error;
            persistentState.State.ErrorMessage = ex.Message;
            await persistentState.WriteStateAsync();

            await eventBus.PublishAsync(new AgentErrorEvent
            {
                SourceId = persistentState.State.AgentId,
                AgentName = persistentState.State.AgentName,
                WorkspaceId = workspaceId,
                ErrorMessage = ex.Message
            }, CancellationToken.None);

            logger.LogError(ex, "Failed to activate agent {AgentName}", persistentState.State.AgentName);
            throw;
        }

        return persistentState.State;
    }

    public async Task DeactivateAsync()
    {
        if (persistentState.State.Status is AgentStatus.Idle or AgentStatus.Deactivating)
            return;

        persistentState.State.Status = AgentStatus.Deactivating;

        var context = new LifecycleContext
        {
            WorkspaceId = persistentState.State.WorkspaceId,
            AgentName = persistentState.State.AgentName,
            Phase = LifecyclePhase.AgentDeactivating
        };

        try
        {
            await lifecycleManager.RunHooksAsync(LifecyclePhase.AgentDeactivating, context, CancellationToken.None);

            persistentState.State.Status = AgentStatus.Idle;
            persistentState.State.DeactivatedAt = DateTimeOffset.UtcNow;
            persistentState.State.ActiveTasks.Clear();
            persistentState.State.ConnectedTools.Clear();

            await persistentState.WriteStateAsync();

            await lifecycleManager.RunHooksAsync(
                LifecyclePhase.AgentDeactivated,
                context with { Phase = LifecyclePhase.AgentDeactivated },
                CancellationToken.None);

            await eventBus.PublishAsync(new AgentDeactivatedEvent
            {
                SourceId = persistentState.State.AgentId,
                AgentName = persistentState.State.AgentName,
                WorkspaceId = persistentState.State.WorkspaceId
            }, CancellationToken.None);

            logger.LogInformation("Agent {AgentName} deactivated", persistentState.State.AgentName);
        }
        catch (Exception ex)
        {
            persistentState.State.Status = AgentStatus.Error;
            persistentState.State.ErrorMessage = ex.Message;
            await persistentState.WriteStateAsync();
            logger.LogError(ex, "Failed to deactivate agent {AgentName}", persistentState.State.AgentName);
            throw;
        }
    }

    public Task<AgentState> GetStateAsync() => Task.FromResult(persistentState.State);

    public async Task<AgentChatResponse> SendAsync(AgentMessage message)
    {
        if (persistentState.State.Status is not (AgentStatus.Active or AgentStatus.Busy))
            throw new InvalidOperationException($"Agent {persistentState.State.AgentName} is not active (status: {persistentState.State.Status}).");

        _chatClient ??= chatClientFactory.Create(persistentState.State.AgentId, persistentState.State.Model);

        var userEntry = new ConversationMessage
        {
            Role = string.IsNullOrWhiteSpace(message.Role) ? "user" : message.Role,
            Content = message.Content,
            Timestamp = DateTimeOffset.UtcNow
        };
        persistentState.State.History.Add(userEntry);
        persistentState.State.LastActive = userEntry.Timestamp;

        var prompt = await GetSystemPromptAsync();
        var chatMessages = new List<ChatMessage>(persistentState.State.History.Count + 1);
        if (!string.IsNullOrWhiteSpace(prompt))
            chatMessages.Add(new ChatMessage(ChatRole.System, prompt));

        foreach (var historyMessage in persistentState.State.History)
            chatMessages.Add(ChatMessageMapper.ToChatMessage(historyMessage));

        var tools = await BuildToolsAsync();
        var options = new ChatOptions
        {
            ModelId = persistentState.State.Model,
            ConversationId = persistentState.State.ConversationId,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["agentId"] = persistentState.State.AgentId
            }
        };

        if (tools.Count > 0)
            options.Tools = tools;

        var response = await _chatClient.GetResponseAsync(chatMessages, options, CancellationToken.None);
        persistentState.State.ConversationId = response.ConversationId ?? persistentState.State.ConversationId;

        var newMessages = new List<ConversationMessage>();
        foreach (var responseMessage in response.Messages)
        {
            foreach (var conversationMessage in ChatMessageMapper.ToConversationMessages(responseMessage))
            {
                persistentState.State.History.Add(conversationMessage);
                newMessages.Add(conversationMessage);
            }
        }

        persistentState.State.LastActive = DateTimeOffset.UtcNow;
        await persistentState.WriteStateAsync();

        return new AgentChatResponse
        {
            Content = response.Text,
            ConversationId = persistentState.State.ConversationId ?? string.Empty,
            Messages = newMessages,
            UsedTools = response.Messages.Any(static m => m.Contents.Any(static c => c is FunctionCallContent or FunctionResultContent)),
            Model = response.ModelId ?? persistentState.State.Model
        };
    }

    public async Task<AgentTaskInfo> SubmitTaskAsync(string description)
    {
        if (persistentState.State.Status is not (AgentStatus.Active or AgentStatus.Busy))
            throw new InvalidOperationException($"Agent {persistentState.State.AgentName} is not active (status: {persistentState.State.Status}).");

        var runningCount = 0;
        foreach (var taskInfo in persistentState.State.ActiveTasks)
        {
            if (taskInfo.Status is AgentTaskStatus.Running)
                runningCount++;
        }

        if (runningCount >= persistentState.State.MaxConcurrentTasks)
        {
            throw new InvalidOperationException(
                $"Agent {persistentState.State.AgentName} has reached max concurrent tasks ({persistentState.State.MaxConcurrentTasks}).");
        }

        var task = new AgentTaskInfo
        {
            TaskId = AgentTaskId.New(),
            Description = description,
            Status = AgentTaskStatus.Running
        };

        persistentState.State.ActiveTasks.Add(task);
        persistentState.State.Status = AgentStatus.Busy;
        persistentState.State.LastActive = DateTimeOffset.UtcNow;
        await persistentState.WriteStateAsync();

        logger.LogInformation("Task {TaskId} submitted to agent {AgentName}", task.TaskId, persistentState.State.AgentName);
        return task;
    }

    public async Task CompleteTaskAsync(AgentTaskId taskId, bool success, ProofOfWork proof)
    {
        AgentTaskInfo? task = null;
        foreach (var candidate in persistentState.State.ActiveTasks)
        {
            if (candidate.TaskId == taskId)
            {
                task = candidate;
                break;
            }
        }

        if (task is null)
            throw new InvalidOperationException($"Task {taskId} not found on agent {persistentState.State.AgentName}.");

        if (!success)
        {
            task.Status = AgentTaskStatus.Failed;
            task.CompletedAt = DateTimeOffset.UtcNow;
            task.Proof = proof;

            UpdateAgentBusyStatus();
            persistentState.State.LastActive = DateTimeOffset.UtcNow;
            await persistentState.WriteStateAsync();

            await eventBus.PublishAsync(new AgentTaskCompletedEvent
            {
                SourceId = persistentState.State.AgentId,
                AgentName = persistentState.State.AgentName,
                WorkspaceId = persistentState.State.WorkspaceId,
                TaskId = taskId
            }, CancellationToken.None);

            logger.LogInformation("Task {TaskId} failed on agent {AgentName}", taskId, persistentState.State.AgentName);
            return;
        }

        task.Status = AgentTaskStatus.AwaitingReview;
        task.Proof = proof;
        persistentState.State.LastActive = DateTimeOffset.UtcNow;
        await persistentState.WriteStateAsync();

        await eventBus.PublishAsync(new AgentTaskAwaitingReviewEvent
        {
            SourceId = persistentState.State.AgentId,
            AgentName = persistentState.State.AgentName,
            WorkspaceId = persistentState.State.WorkspaceId,
            TaskId = taskId
        }, CancellationToken.None);

        logger.LogInformation(
            "Task {TaskId} awaiting review on agent {AgentName} ({ProofCount} proof items)",
            taskId,
            persistentState.State.AgentName,
            proof.Items.Count);

        var verifier = grainFactory.GetGrain<IProofVerifierGrain>(persistentState.State.WorkspaceId.ToString());
        _ = verifier.VerifyAsync(
            persistentState.State.WorkspaceId,
            persistentState.State.AgentName,
            taskId,
            proof);
    }

    public async Task ReviewTaskAsync(AgentTaskId taskId, bool accepted, string? feedback = null, VerificationRecord? verification = null)
    {
        AgentTaskInfo? task = null;
        foreach (var candidate in persistentState.State.ActiveTasks)
        {
            if (candidate.TaskId == taskId)
            {
                task = candidate;
                break;
            }
        }

        if (task is null)
            throw new InvalidOperationException($"Task {taskId} not found on agent {persistentState.State.AgentName}.");

        if (task.Status is not AgentTaskStatus.AwaitingReview)
            throw new InvalidOperationException($"Task {taskId} is not awaiting review (status: {task.Status}).");

        if (task.Proof is not null)
        {
            task.Proof.ReviewFeedback = feedback;
            task.Proof.ReviewedAt = DateTimeOffset.UtcNow;
            if (verification is not null)
                task.Proof.Verification = verification;
        }

        if (accepted)
        {
            task.Status = AgentTaskStatus.Accepted;
            task.CompletedAt = DateTimeOffset.UtcNow;
            persistentState.State.TotalTasksCompleted++;
        }
        else
        {
            task.Status = AgentTaskStatus.Rejected;
        }

        UpdateAgentBusyStatus();
        persistentState.State.LastActive = DateTimeOffset.UtcNow;
        await persistentState.WriteStateAsync();

        await eventBus.PublishAsync(new AgentTaskReviewedEvent
        {
            SourceId = persistentState.State.AgentId,
            AgentName = persistentState.State.AgentName,
            WorkspaceId = persistentState.State.WorkspaceId,
            TaskId = taskId,
            Accepted = accepted
        }, CancellationToken.None);

        logger.LogInformation(
            "Task {TaskId} reviewed on agent {AgentName} (accepted: {Accepted})",
            taskId,
            persistentState.State.AgentName,
            accepted);
    }

    public async Task ConnectToolAsync(string toolName)
    {
        if (!persistentState.State.ConnectedTools.Contains(toolName, StringComparer.Ordinal))
            persistentState.State.ConnectedTools.Add(toolName);

        await persistentState.WriteStateAsync();
    }

    public async Task DisconnectToolAsync(string toolName)
    {
        persistentState.State.ConnectedTools.Remove(toolName);
        await persistentState.WriteStateAsync();
    }

    private void EnsureIdentity(WorkspaceId workspaceId)
    {
        if (!string.IsNullOrWhiteSpace(persistentState.State.AgentId))
        {
            if (persistentState.State.WorkspaceId.IsEmpty)
                persistentState.State.WorkspaceId = workspaceId;

            if (string.IsNullOrWhiteSpace(persistentState.State.AgentName))
                persistentState.State.AgentName = GetAgentName(persistentState.State.AgentId);

            return;
        }

        ApplyIdentity(TryGetPrimaryKeyString(), workspaceId);
    }

    private void UpdateAgentBusyStatus()
    {
        var hasRunning = false;
        foreach (var activeTask in persistentState.State.ActiveTasks)
        {
            if (activeTask.Status is AgentTaskStatus.Running)
            {
                hasRunning = true;
                break;
            }
        }

        if (!hasRunning)
            persistentState.State.Status = AgentStatus.Active;
    }

    private void ApplyIdentity(string? key, WorkspaceId workspaceId)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            var parts = key.Split('/', 2);
            persistentState.State.AgentId = key;
            persistentState.State.WorkspaceId = WorkspaceId.From(parts.Length > 1 ? parts[0] : key);
            persistentState.State.AgentName = parts.Length > 1 ? parts[1] : key;
            return;
        }

        persistentState.State.WorkspaceId = workspaceId;
        persistentState.State.AgentName = string.IsNullOrWhiteSpace(persistentState.State.AgentName)
            ? "agent"
            : persistentState.State.AgentName;
        persistentState.State.AgentId = $"{workspaceId}/{persistentState.State.AgentName}";
    }

    private static string GetAgentName(string agentId)
    {
        var separatorIndex = agentId.IndexOf('/', StringComparison.Ordinal);
        return separatorIndex >= 0 && separatorIndex < agentId.Length - 1
            ? agentId[(separatorIndex + 1)..]
            : agentId;
    }

    private string? TryGetPrimaryKeyString()
    {
        try
        {
            return this.GetPrimaryKeyString();
        }
        catch (NullReferenceException)
        {
            return null;
        }
    }

    private async Task<string?> GetSystemPromptAsync()
    {
        if (_systemPrompt is not null || persistentState.State.Definition?.SystemPromptFile is null)
            return _systemPrompt;

        var promptPath = persistentState.State.Definition.SystemPromptFile;
        if (!File.Exists(promptPath))
        {
            logger.LogWarning("System prompt file '{PromptPath}' was not found for agent {AgentName}", promptPath, persistentState.State.AgentName);
            _systemPrompt = string.Empty;
            return _systemPrompt;
        }

        _systemPrompt = await File.ReadAllTextAsync(promptPath);
        return _systemPrompt;
    }

    private async Task<List<AITool>> BuildToolsAsync()
    {
        var registry = grainFactory.GetGrain<IToolRegistryGrain>(persistentState.State.WorkspaceId.ToString());
        var tools = new List<AITool>(persistentState.State.ConnectedTools.Count);

        foreach (var toolName in persistentState.State.ConnectedTools)
        {
            var resolution = await registry.ResolveAsync(persistentState.State.AgentName, toolName);
            if (resolution is null)
                continue;

            Func<string, Task<string>> toolDelegate = input => InvokeToolAsync(toolName, input);
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

    private async Task<string> InvokeToolAsync(string toolName, string input)
    {
        var registry = grainFactory.GetGrain<IToolRegistryGrain>(persistentState.State.WorkspaceId.ToString());
        var resolution = await registry.ResolveAsync(persistentState.State.AgentName, toolName)
            ?? throw new InvalidOperationException($"Tool '{toolName}' is not available to agent '{persistentState.State.AgentName}'.");

        var toolGrain = grainFactory.GetGrain<IToolGrain>(resolution.GrainKey);
        var invocation = ToolInvocationBuilder.FromInput(toolName, input);
        var result = await toolGrain.InvokeAsync(invocation, resolution.Token);
        return result.Success ? result.Output : $"Tool '{toolName}' failed: {result.Error}";
    }
}
