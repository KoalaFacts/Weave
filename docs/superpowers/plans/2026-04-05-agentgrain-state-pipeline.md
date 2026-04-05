# AgentGrain State Methods + Chat Pipeline Extraction — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract task management into `AgentState` mutation methods and chat pipeline into `AgentChatPipeline` behind `IAgentChatPipeline`, reducing `AgentGrain` from 523 to ~280 lines.

**Architecture:** Pure state mutation methods on `AgentState` absorb task lookup, validation, and transitions. `AgentChatPipeline` (implementing `IAgentChatPipeline`) absorbs system prompt loading, tool resolution, chat client interaction, and message conversion. `AgentGrain` becomes a thin orchestrator: validate → delegate → persist → events → log.

**Tech Stack:** C# / .NET 10, Orleans, Microsoft.Extensions.AI, xunit.v3 + Shouldly + NSubstitute

---

## File Map

| Action | File | Responsibility |
|--------|------|----------------|
| Modify | `src/Assistants/Weave.Agents/Models/AgentState.cs` | Add task mutation methods |
| Create | `src/Assistants/Weave.Agents.Tests/AgentStateTaskMethodTests.cs` | Tests for state methods |
| Create | `src/Assistants/Weave.Agents/Pipeline/IAgentChatPipeline.cs` | Chat pipeline interface |
| Create | `src/Assistants/Weave.Agents/Pipeline/AgentChatPipeline.cs` | Chat pipeline implementation |
| Create | `src/Assistants/Weave.Agents.Tests/AgentChatPipelineTests.cs` | Tests for pipeline |
| Modify | `src/Assistants/Weave.Agents/Grains/AgentGrain.cs` | Rewire to use state methods + pipeline |
| Modify | `src/Assistants/Weave.Agents.Tests/AgentGrainTests.cs` | Update CreateGrain helper |
| Modify | `src/Runtime/Weave.Silo/Program.cs:84` | Register `IAgentChatPipeline` |

---

### Task 1: AgentState Task Methods — Implementation and Tests

**Files:**
- Modify: `src/Assistants/Weave.Agents/Models/AgentState.cs`
- Create: `src/Assistants/Weave.Agents.Tests/AgentStateTaskMethodTests.cs`

- [ ] **Step 1: Write the AgentState task methods**

Add these methods to the `AgentState` record in `src/Assistants/Weave.Agents/Models/AgentState.cs`, after the existing properties (after line 25):

```csharp
public int RunningTaskCount
{
    get
    {
        var count = 0;
        foreach (var task in ActiveTasks)
        {
            if (task.Status is AgentTaskStatus.Running)
                count++;
        }
        return count;
    }
}

public AgentTaskInfo GetTask(AgentTaskId taskId)
{
    foreach (var task in ActiveTasks)
    {
        if (task.TaskId == taskId)
            return task;
    }
    throw new InvalidOperationException($"Task {taskId} not found.");
}

public AgentTaskInfo SubmitTask(string description)
{
    if (RunningTaskCount >= MaxConcurrentTasks)
        throw new InvalidOperationException($"Max concurrent tasks ({MaxConcurrentTasks}) reached.");

    var task = new AgentTaskInfo
    {
        TaskId = AgentTaskId.New(),
        Description = description,
        Status = AgentTaskStatus.Running
    };

    ActiveTasks.Add(task);
    Status = AgentStatus.Busy;
    LastActive = DateTimeOffset.UtcNow;
    return task;
}

public void FailTask(AgentTaskId taskId, ProofOfWork proof)
{
    var task = GetTask(taskId);
    task.Status = AgentTaskStatus.Failed;
    task.CompletedAt = DateTimeOffset.UtcNow;
    task.Proof = proof;
    RefreshBusyStatus();
    LastActive = DateTimeOffset.UtcNow;
}

public void SetAwaitingReview(AgentTaskId taskId, ProofOfWork proof)
{
    var task = GetTask(taskId);
    task.Status = AgentTaskStatus.AwaitingReview;
    task.Proof = proof;
    LastActive = DateTimeOffset.UtcNow;
}

public void AcceptTask(AgentTaskId taskId, string? feedback, VerificationRecord? verification)
{
    var task = GetTask(taskId);
    if (task.Status is not AgentTaskStatus.AwaitingReview)
        throw new InvalidOperationException($"Task {taskId} is not awaiting review (status: {task.Status}).");

    ApplyReviewMetadata(task, feedback, verification);
    task.Status = AgentTaskStatus.Accepted;
    task.CompletedAt = DateTimeOffset.UtcNow;
    TotalTasksCompleted++;
    RefreshBusyStatus();
    LastActive = DateTimeOffset.UtcNow;
}

public void RejectTask(AgentTaskId taskId, string? feedback, VerificationRecord? verification)
{
    var task = GetTask(taskId);
    if (task.Status is not AgentTaskStatus.AwaitingReview)
        throw new InvalidOperationException($"Task {taskId} is not awaiting review (status: {task.Status}).");

    ApplyReviewMetadata(task, feedback, verification);
    task.Status = AgentTaskStatus.Rejected;
    RefreshBusyStatus();
    LastActive = DateTimeOffset.UtcNow;
}

public void RefreshBusyStatus()
{
    if (RunningTaskCount == 0)
        Status = AgentStatus.Active;
}

private static void ApplyReviewMetadata(AgentTaskInfo task, string? feedback, VerificationRecord? verification)
{
    if (task.Proof is null)
        return;

    task.Proof.ReviewFeedback = feedback;
    task.Proof.ReviewedAt = DateTimeOffset.UtcNow;
    if (verification is not null)
        task.Proof.Verification = verification;
}
```

- [ ] **Step 2: Write the AgentState task method tests**

Create `src/Assistants/Weave.Agents.Tests/AgentStateTaskMethodTests.cs`:

```csharp
using Weave.Agents.Models;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

public sealed class AgentStateTaskMethodTests
{
    private static AgentState CreateActiveState(int maxTasks = 2) =>
        new()
        {
            AgentId = "ws-1/researcher",
            WorkspaceId = WorkspaceId.From("ws-1"),
            AgentName = "researcher",
            Status = AgentStatus.Active,
            MaxConcurrentTasks = maxTasks
        };

    private static ProofOfWork CreateProof(string label = "CI", string value = "passed") =>
        new()
        {
            Items = [new ProofItem { Type = ProofType.CiStatus, Label = label, Value = value }]
        };

    // --- GetTask ---

    [Fact]
    public void GetTask_ExistingId_ReturnsTask()
    {
        var state = CreateActiveState();
        var submitted = state.SubmitTask("Fix bug");

        var found = state.GetTask(submitted.TaskId);

        found.ShouldBeSameAs(submitted);
    }

    [Fact]
    public void GetTask_UnknownId_Throws()
    {
        var state = CreateActiveState();

        var ex = Should.Throw<InvalidOperationException>(() => state.GetTask(AgentTaskId.From("nonexistent")));
        ex.Message.ShouldContain("not found");
    }

    // --- SubmitTask ---

    [Fact]
    public void SubmitTask_UnderCapacity_ReturnsRunningTask()
    {
        var state = CreateActiveState();

        var task = state.SubmitTask("Fix bug");

        task.TaskId.IsEmpty.ShouldBeFalse();
        task.Description.ShouldBe("Fix bug");
        task.Status.ShouldBe(AgentTaskStatus.Running);
    }

    [Fact]
    public void SubmitTask_UnderCapacity_SetsBusyStatus()
    {
        var state = CreateActiveState();

        state.SubmitTask("Fix bug");

        state.Status.ShouldBe(AgentStatus.Busy);
    }

    [Fact]
    public void SubmitTask_AtCapacity_Throws()
    {
        var state = CreateActiveState(maxTasks: 1);
        state.SubmitTask("Task 1");

        var ex = Should.Throw<InvalidOperationException>(() => state.SubmitTask("Task 2"));
        ex.Message.ShouldContain("Max concurrent");
    }

    // --- FailTask ---

    [Fact]
    public void FailTask_SetsFailedStatus()
    {
        var state = CreateActiveState();
        var task = state.SubmitTask("Fix bug");

        state.FailTask(task.TaskId, CreateProof());

        var updated = state.GetTask(task.TaskId);
        updated.Status.ShouldBe(AgentTaskStatus.Failed);
        updated.CompletedAt.ShouldNotBeNull();
        updated.Proof.ShouldNotBeNull();
    }

    [Fact]
    public void FailTask_NoRunningTasks_RefreshesToActive()
    {
        var state = CreateActiveState();
        var task = state.SubmitTask("Fix bug");

        state.FailTask(task.TaskId, CreateProof());

        state.Status.ShouldBe(AgentStatus.Active);
    }

    // --- SetAwaitingReview ---

    [Fact]
    public void SetAwaitingReview_SetsStatusAndProof()
    {
        var state = CreateActiveState();
        var task = state.SubmitTask("Fix bug");

        state.SetAwaitingReview(task.TaskId, CreateProof());

        var updated = state.GetTask(task.TaskId);
        updated.Status.ShouldBe(AgentTaskStatus.AwaitingReview);
        updated.Proof.ShouldNotBeNull();
    }

    // --- AcceptTask ---

    [Fact]
    public void AcceptTask_SetsAcceptedAndCompletedAt()
    {
        var state = CreateActiveState();
        var task = state.SubmitTask("Fix bug");
        state.SetAwaitingReview(task.TaskId, CreateProof());

        state.AcceptTask(task.TaskId, "Looks good", null);

        var updated = state.GetTask(task.TaskId);
        updated.Status.ShouldBe(AgentTaskStatus.Accepted);
        updated.CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public void AcceptTask_IncrementsTotalCompleted()
    {
        var state = CreateActiveState();
        var task = state.SubmitTask("Fix bug");
        state.SetAwaitingReview(task.TaskId, CreateProof());

        state.AcceptTask(task.TaskId, null, null);

        state.TotalTasksCompleted.ShouldBe(1);
    }

    [Fact]
    public void AcceptTask_WhenNotAwaitingReview_Throws()
    {
        var state = CreateActiveState();
        var task = state.SubmitTask("Fix bug");

        var ex = Should.Throw<InvalidOperationException>(() => state.AcceptTask(task.TaskId, null, null));
        ex.Message.ShouldContain("not awaiting review");
    }

    [Fact]
    public void AcceptTask_AppliesReviewMetadata()
    {
        var state = CreateActiveState();
        var task = state.SubmitTask("Fix bug");
        state.SetAwaitingReview(task.TaskId, CreateProof());

        state.AcceptTask(task.TaskId, "LGTM", null);

        var updated = state.GetTask(task.TaskId);
        updated.Proof.ShouldNotBeNull();
        updated.Proof!.ReviewFeedback.ShouldBe("LGTM");
        updated.Proof.ReviewedAt.ShouldNotBeNull();
    }

    // --- RejectTask ---

    [Fact]
    public void RejectTask_SetsRejectedStatus()
    {
        var state = CreateActiveState();
        var task = state.SubmitTask("Fix bug");
        state.SetAwaitingReview(task.TaskId, CreateProof());

        state.RejectTask(task.TaskId, "CI is red", null);

        var updated = state.GetTask(task.TaskId);
        updated.Status.ShouldBe(AgentTaskStatus.Rejected);
        updated.CompletedAt.ShouldBeNull();
    }

    [Fact]
    public void RejectTask_WhenNotAwaitingReview_Throws()
    {
        var state = CreateActiveState();
        var task = state.SubmitTask("Fix bug");

        var ex = Should.Throw<InvalidOperationException>(() => state.RejectTask(task.TaskId, null, null));
        ex.Message.ShouldContain("not awaiting review");
    }

    // --- RunningTaskCount ---

    [Fact]
    public void RunningTaskCount_ReturnsCorrectCount()
    {
        var state = CreateActiveState(maxTasks: 3);
        state.SubmitTask("Task 1");
        state.SubmitTask("Task 2");

        state.RunningTaskCount.ShouldBe(2);
    }

    // --- RefreshBusyStatus ---

    [Fact]
    public void RefreshBusyStatus_NoRunning_SetsActive()
    {
        var state = CreateActiveState();
        var task = state.SubmitTask("Task");
        state.FailTask(task.TaskId, CreateProof());

        state.Status.ShouldBe(AgentStatus.Active);
    }

    [Fact]
    public void RefreshBusyStatus_HasRunning_StaysBusy()
    {
        var state = CreateActiveState(maxTasks: 2);
        state.SubmitTask("Task 1");
        var task2 = state.SubmitTask("Task 2");
        state.FailTask(task2.TaskId, CreateProof());

        state.Status.ShouldBe(AgentStatus.Busy);
    }
}
```

- [ ] **Step 3: Run the tests**

Run: `dotnet test --project src/Assistants/Weave.Agents.Tests --filter "AgentStateTaskMethodTests"`
Expected: All 17 tests PASS

- [ ] **Step 4: Commit**

```bash
git add src/Assistants/Weave.Agents/Models/AgentState.cs src/Assistants/Weave.Agents.Tests/AgentStateTaskMethodTests.cs
git commit -m "feat: add task mutation methods to AgentState"
```

---

### Task 2: IAgentChatPipeline Interface and AgentChatPipeline Implementation

**Files:**
- Create: `src/Assistants/Weave.Agents/Pipeline/IAgentChatPipeline.cs`
- Create: `src/Assistants/Weave.Agents/Pipeline/AgentChatPipeline.cs`

- [ ] **Step 1: Create the IAgentChatPipeline interface**

Create `src/Assistants/Weave.Agents/Pipeline/IAgentChatPipeline.cs`:

```csharp
using Weave.Agents.Models;

namespace Weave.Agents.Pipeline;

public interface IAgentChatPipeline
{
    void Initialize(string agentId, string? model);
    void Reset();
    Task<AgentChatResponse> ExecuteAsync(AgentState state, AgentMessage message);
}
```

- [ ] **Step 2: Create the AgentChatPipeline implementation**

Create `src/Assistants/Weave.Agents/Pipeline/AgentChatPipeline.cs`. This lifts `SendAsync` body (lines 172-229), `GetSystemPromptAsync` (lines 470-485), `BuildToolsAsync` (lines 487-510), and `InvokeToolAsync` (lines 512-522) from `AgentGrain.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Weave.Agents.Models;
using Weave.Tools.Builders;
using Weave.Tools.Grains;
using Weave.Tools.Models;

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
```

- [ ] **Step 3: Build to verify no compile errors**

Run: `dotnet build Weave.slnx`
Expected: Build succeeded, 0 errors

- [ ] **Step 4: Commit**

```bash
git add src/Assistants/Weave.Agents/Pipeline/IAgentChatPipeline.cs src/Assistants/Weave.Agents/Pipeline/AgentChatPipeline.cs
git commit -m "feat: add IAgentChatPipeline interface and AgentChatPipeline implementation"
```

---

### Task 3: AgentChatPipeline Tests

**Files:**
- Create: `src/Assistants/Weave.Agents.Tests/AgentChatPipelineTests.cs`

- [ ] **Step 1: Write the AgentChatPipeline tests**

Create `src/Assistants/Weave.Agents.Tests/AgentChatPipelineTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Models;
using Weave.Agents.Pipeline;
using Weave.Shared.Ids;
using Weave.Tools.Grains;
using Weave.Tools.Models;
using Weave.Workspaces.Models;

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

        var grainFactory = Substitute.For<IGrainFactory>();
        var logger = NullLogger<AgentChatPipeline>.Instance;

        var pipeline = new AgentChatPipeline(grainFactory, chatClientFactory, logger);
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
        var grainFactory = Substitute.For<IGrainFactory>();
        var pipeline = new AgentChatPipeline(grainFactory, chatClientFactory, NullLogger<AgentChatPipeline>.Instance);

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

        // ExecuteAsync should re-create the client via the factory since Reset cleared it
        await chatClient.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithConnectedTools_BuildsToolOptions()
    {
        var (pipeline, chatClient) = CreatePipeline();
        var state = CreateActiveState();
        state.ConnectedTools.Add("code-search");

        var registry = Substitute.For<IToolRegistryGrain>();
        registry.ResolveAsync("researcher", "code-search")
            .Returns(Task.FromResult<ToolResolution?>(null));

        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IToolRegistryGrain>(Arg.Any<string>(), Arg.Any<string?>()).Returns(registry);

        var chatClientFactory = Substitute.For<IAgentChatClientFactory>();
        chatClientFactory.Create(Arg.Any<string>(), Arg.Any<string?>()).Returns(chatClient);

        var pipelineWithTools = new AgentChatPipeline(grainFactory, chatClientFactory, NullLogger<AgentChatPipeline>.Instance);

        await pipelineWithTools.ExecuteAsync(state, new AgentMessage { Content = "Search for X" });

        await registry.Received(1).ResolveAsync("researcher", "code-search");
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test --project src/Assistants/Weave.Agents.Tests --filter "AgentChatPipelineTests"`
Expected: All 8 tests PASS

- [ ] **Step 3: Commit**

```bash
git add src/Assistants/Weave.Agents.Tests/AgentChatPipelineTests.cs
git commit -m "test: add AgentChatPipeline tests"
```

---

### Task 4: Rewire AgentGrain and Register Pipeline

**Files:**
- Modify: `src/Assistants/Weave.Agents/Grains/AgentGrain.cs`
- Modify: `src/Assistants/Weave.Agents.Tests/AgentGrainTests.cs`
- Modify: `src/Runtime/Weave.Silo/Program.cs:84`

- [ ] **Step 1: Register IAgentChatPipeline in the Silo**

In `src/Runtime/Weave.Silo/Program.cs`, add after line 84 (after the `IAgentChatClientFactory` registration):

```csharp
builder.Services.AddTransient<IAgentChatPipeline, AgentChatPipeline>();
```

Add the using at the top of the file if not already present:

```csharp
using Weave.Agents.Pipeline;
```

- [ ] **Step 2: Rewrite AgentGrain to use state methods and pipeline**

Replace the entire content of `src/Assistants/Weave.Agents/Grains/AgentGrain.cs` with:

```csharp
using Microsoft.Extensions.Logging;
using Weave.Agents.Events;
using Weave.Agents.Models;
using Weave.Agents.Pipeline;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Models;

namespace Weave.Agents.Grains;

public sealed class AgentGrain(
    IGrainFactory grainFactory,
    IAgentChatPipeline chatPipeline,
    ILifecycleManager lifecycleManager,
    IEventBus eventBus,
    ILogger<AgentGrain> logger,
    [PersistentState("agent", "Default")] IPersistentState<AgentState> persistentState) : Grain, IAgentGrain
{
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
            chatPipeline.Initialize(persistentState.State.AgentId, persistentState.State.Model);
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

            chatPipeline.Reset();
            chatPipeline.Initialize(persistentState.State.AgentId, definition.Model);

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

            chatPipeline.Reset();

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

        var response = await chatPipeline.ExecuteAsync(persistentState.State, message);
        await persistentState.WriteStateAsync();
        return response;
    }

    public async Task<AgentTaskInfo> SubmitTaskAsync(string description)
    {
        if (persistentState.State.Status is not (AgentStatus.Active or AgentStatus.Busy))
            throw new InvalidOperationException($"Agent {persistentState.State.AgentName} is not active (status: {persistentState.State.Status}).");

        var task = persistentState.State.SubmitTask(description);
        await persistentState.WriteStateAsync();

        logger.LogInformation("Task {TaskId} submitted to agent {AgentName}", task.TaskId, persistentState.State.AgentName);
        return task;
    }

    public async Task CompleteTaskAsync(AgentTaskId taskId, bool success, ProofOfWork proof)
    {
        if (!success)
        {
            persistentState.State.FailTask(taskId, proof);
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

        persistentState.State.SetAwaitingReview(taskId, proof);
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
        if (accepted)
            persistentState.State.AcceptTask(taskId, feedback, verification);
        else
            persistentState.State.RejectTask(taskId, feedback, verification);

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
}
```

- [ ] **Step 3: Update AgentGrainTests.CreateGrain helper**

In `src/Assistants/Weave.Agents.Tests/AgentGrainTests.cs`, update the `CreateGrain` method. Replace `IAgentChatClientFactory` with `IAgentChatPipeline`:

Replace the existing `CreateGrain` method (lines 44-57) with:

```csharp
    private static (AgentGrain Grain, ILifecycleManager Lifecycle, IEventBus EventBus) CreateGrain()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var chatPipeline = Substitute.For<IAgentChatPipeline>();
        var lifecycle = Substitute.For<ILifecycleManager>();
        var eventBus = Substitute.For<IEventBus>();
        var logger = Substitute.For<ILogger<AgentGrain>>();
        var persistentState = CreatePersistentState();

        var grain = new AgentGrain(grainFactory, chatPipeline, lifecycle, eventBus, logger, persistentState);
        return (grain, lifecycle, eventBus);
    }
```

Also add the using at the top if not already present:

```csharp
using Weave.Agents.Pipeline;
```

- [ ] **Step 4: Build the full solution**

Run: `dotnet build Weave.slnx`
Expected: Build succeeded, 0 errors

- [ ] **Step 5: Run all tests**

Run: `dotnet test --solution Weave.slnx`
Expected: All tests PASS. The 20 existing `AgentGrainTests` plus 17 `AgentStateTaskMethodTests` plus 8 `AgentChatPipelineTests` should all pass.

- [ ] **Step 6: Commit**

```bash
git add src/Assistants/Weave.Agents/Grains/AgentGrain.cs src/Assistants/Weave.Agents.Tests/AgentGrainTests.cs src/Runtime/Weave.Silo/Program.cs
git commit -m "refactor: rewire AgentGrain to use AgentState methods and IAgentChatPipeline"
```
