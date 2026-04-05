# Assistants

> **Source**: `src/Assistants/` | **Depends on**: [Foundation](foundation.md), [Workspaces](workspaces.md), [Tools](tools.md), [Security](security.md) | **Depended on by**: [Runtime](runtime.md), [UX](ux.md)
> **See also**: [index](index.md)

The Assistants subsystem implements the AI agent framework — agent grains, supervisor, heartbeat scheduling, chat pipeline, task management, and proof-of-work verification.

## Projects

| Project | Purpose |
|---------|---------|
| `Weave.Agents` | Agent grains, supervisor, heartbeat, chat pipeline, commands, queries, events |
| `Weave.Agents.Tests` | 15 test files covering grains, pipeline, commands, static helpers |

## Agent Grain

**Key**: `{workspaceId}/{agentName}` — **State**: `[PersistentState("agent", "Default")]`

### Interface

```csharp
public interface IAgentGrain : IGrainWithStringKey
{
    Task ActivateAgentAsync(WorkspaceId workspaceId, AgentDefinition definition);
    Task DeactivateAsync();
    Task<AgentState> GetStateAsync();
    Task<AgentChatResponse> SendAsync(string message);
    Task<AgentTaskInfo> SubmitTaskAsync(string description);
    Task<AgentTaskInfo> CompleteTaskAsync(AgentTaskId taskId, bool success, ProofOfWork? proof);
    Task<AgentTaskInfo> ReviewTaskAsync(AgentTaskId taskId, bool accepted, string? feedback);
    Task ConnectToolAsync(string toolName);
    Task DisconnectToolAsync(string toolName);
}
```

### State Transitions

```
Idle → Activating → Active ↔ Busy → Deactivating → Idle
                                ↓
                              Error
```

### Agent State

```csharp
AgentState {
    AgentId: string                 // "{workspaceId}/{agentName}"
    WorkspaceId: WorkspaceId
    AgentName, Model: string
    Status: AgentStatus             // Idle, Activating, Active, Busy, Deactivating, Error
    ConnectedTools: List<string>
    ActiveTasks: List<AgentTaskInfo>
    MaxConcurrentTasks: int
    History: List<ConversationMessage>
    TotalTasksCompleted: int
    Definition: AgentDefinition?
    ConversationId: string?
    ActivatedAt, DeactivatedAt, LastActive: DateTimeOffset?
    ErrorMessage: string?
}
```

## Supervisor Grain

**Key**: `{workspaceId}` — **State**: `[PersistentState("agent-supervisor", "Default")]`

Manages agents as a group within a workspace.

```csharp
public interface IAgentSupervisorGrain : IGrainWithStringKey
{
    Task ActivateAllAsync(WorkspaceManifest manifest);
    Task DeactivateAllAsync();
    Task<IReadOnlyList<AgentState>> GetAllAgentStatesAsync();
    Task<AgentState> GetAgentStateAsync(string agentName);
}
```

**Activation flow**: reads manifest → creates agent grains → resolves tool connections via `ToolRegistryGrain` → connects approved tools → stores agent names.

## Heartbeat

**Key**: `{workspaceId}/{agentName}` — enables agents to periodically perform autonomous tasks.

### Interface

```csharp
public interface IHeartbeatGrain : IGrainWithStringKey
{
    Task StartAsync(HeartbeatConfig config);
    Task StopAsync();
    Task<HeartbeatState> GetStateAsync();
}
```

### Behavior

- Uses Orleans `RegisterGrainTimer()` for scheduling.
- Parses simple cron patterns (`*/N * * * *`), falls back to 30 minutes.
- On each tick: verifies agent is active → submits tasks → sends messages → creates proof → completes tasks automatically.
- Gracefully backs off on max capacity.

### State

```csharp
HeartbeatState {
    IsRunning: bool
    Config: HeartbeatConfig
    LastRun, NextRun: DateTimeOffset?
    ExecutionCount: int
}
```

## Chat Pipeline

The `AgentChatClientFactory.Create()` builds a layered middleware pipeline:

```
ChatClientBuilder (function invocation support)
    ↓
CostTrackingChatClient (records token usage per agent/model)
    ↓
RateLimitingChatClient (token bucket: 60 req/min, queue: 10)
    ↓
FallbackChatClient (local testing/demo without real LLM)
```

### Message Flow (SendAsync)

1. Validate agent is Active or Busy
2. Add user message to conversation history
3. Load system prompt from file if configured
4. Resolve connected tools via `BuildToolsAsync()`
5. Prepare chat messages (system + history)
6. Call `_chatClient.GetResponseAsync()`
7. Process response — extract text, tool calls/results
8. Store in history, update persistent state
9. Return `AgentChatResponse`

### RateLimitingChatClient

- Token bucket: 60 requests/minute per agent
- Queue: max 10 pending, oldest-first
- Throws `InvalidOperationException` when limit exceeded

### CostTrackingChatClient

- Records input/output tokens and request count per agent/model
- In-memory `ConcurrentDictionary` (ephemeral per session)
- Exposes `GetCostSummary(agentId)` and `GetAllCosts()`

### FallbackChatClient

For local testing without real LLM calls:
- Detects `/tool <name> <input>` prefix for synthetic function calls
- Echoes user input with model ID prefix
- Pseudo-tokenization (word count)

## Task Management

### Task State Transitions

```
Pending → Running → AwaitingReview → Accepted / Rejected / Failed
                                   ↗
                        Cancelled ←
```

### AgentTaskInfo

```csharp
AgentTaskInfo {
    TaskId: AgentTaskId
    Description: string
    Status: AgentTaskStatus
    CreatedAt, CompletedAt: DateTimeOffset
    ResultSummary: string?
    Proof: ProofOfWork?
}
```

### ProofOfWork

```csharp
ProofOfWork {
    Items: List<ProofItem>          // Types: CiStatus, TestResults, PullRequest, CodeReview, DiffSummary, Custom
    SubmittedAt: DateTimeOffset
    ReviewFeedback: string?
    Verification: VerificationRecord?
}
```

## Proof Verification

Multi-validator consensus system using LLM-based evaluation.

### ProofVerifierGrain (Orchestrator)

1. Configures 6 default verification conditions (ci-passing, tests-passing, pr-has-link, code-review-present, diff-present, custom-present)
2. Dispatches N independent `ProofValidatorGrain` instances
3. Each validator evaluates proof independently
4. Majority vote determines acceptance (> N/2)
5. Calls `AgentGrain.ReviewTaskAsync()` with result

### ProofValidatorGrain (Evaluator)

1. Builds markdown summary of proof items + conditions
2. Uses LLM to evaluate each condition (JSON output)
3. Adds strictness check: all proof items must have non-empty values
4. Returns `VerificationVote` with condition results

## Tool Registry Grain

**Key**: `{workspaceId}` — **State**: `[PersistentState("tool-registry", "Default")]`

Manages per-workspace tool connections and agent-to-tool permissions.

### Resolution Flow

1. Verify agent has permission for the tool
2. Retrieve tool definition from state
3. Lazy-connect if not yet connected (lifecycle hooks + event)
4. Mint capability token via `ICapabilityTokenService`
5. Get tool schema from tool grain
6. Return `ToolResolution` (grain key, token, schema)

## Commands & Queries

### Commands

| Command | Result | Behavior |
|---------|--------|----------|
| `ActivateAgentCommand` | `AgentState` | Grants tools, activates grain, starts heartbeat |
| `DeactivateAgentCommand` | `bool` | Stops heartbeat, deactivates grain |
| `SendAgentMessageCommand` | `AgentChatResponse` | Forwards to `IAgentGrain.SendAsync()` |
| `SubmitAgentTaskCommand` | `AgentTaskInfo` | Creates task via grain |
| `CompleteAgentTaskCommand` | `AgentTaskInfo` | Marks complete, triggers verification |
| `ReviewAgentTaskCommand` | `AgentTaskInfo` | Finalizes with accept/reject |

### Queries

| Query | Result |
|-------|--------|
| `GetAgentStateQuery` | `AgentState` |
| `GetAllAgentStatesQuery` | `IReadOnlyList<AgentState>` |

## Events

| Event | Description |
|-------|-------------|
| `AgentActivatedEvent` | Agent ready (includes model, tools) |
| `AgentDeactivatedEvent` | Agent shutdown |
| `AgentErrorEvent` | Activation/operation failure |
| `AgentTaskCompletedEvent` | Task done (success/fail) |
| `AgentTaskAwaitingReviewEvent` | Task submitted for verification |
| `AgentTaskReviewedEvent` | Review complete (accept/reject) |
| `ProofVerifiedEvent` | Verification consensus reached |
| `ToolConnectedEvent` | Tool activated |
| `ToolDisconnectedEvent` | Tool deactivated |
| `ToolErrorEvent` | Connection failure |

## Testing

15 test files covering:
- **Grain tests**: AgentGrain, SupervisorGrain, HeartbeatGrain, ProofVerifier, ProofValidator, ToolRegistry
- **Command/query handler tests**: dispatch paths, orchestration flows
- **Pipeline tests**: CostTracking, RateLimiting, FallbackChatClient
- **Static method tests**: tool invocation parsing, tool description building, message conversion
- **Cost ledger tests**: recording and summary retrieval
