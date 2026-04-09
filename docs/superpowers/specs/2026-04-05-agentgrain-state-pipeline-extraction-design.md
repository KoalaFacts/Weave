# AgentGrain State Methods + Chat Pipeline Extraction

**Date:** 2026-04-05
**Status:** Approved
**Scope:** Extract task management into `AgentState` mutation methods and chat pipeline into `AgentChatPipeline` behind `IAgentChatPipeline`, reducing `AgentGrain` from 523 to ~280 lines.

## Problem

After the static helper extraction, `AgentGrain` (523 lines) still mixes three concerns:

1. **Task management state logic** — `SubmitTaskAsync`, `CompleteTaskAsync`, `ReviewTaskAsync` contain duplicated task-find loops, validation, and status transitions that are pure state operations buried inside grain orchestration.
2. **Chat pipeline** — `SendAsync`, `GetSystemPromptAsync`, `BuildToolsAsync`, `InvokeToolAsync` couple system prompt I/O, tool resolution, and message building. The grain directly holds `IChatClient` and `_systemPrompt` cache fields.
3. **Grain orchestration** — lifecycle hooks, persistence, event publishing, logging. This is what the grain *should* be.

The grain also violates the project's constructor injection convention: it directly instantiates and manages `IChatClient` instead of depending on an abstraction for the chat pipeline.

## Design

### 1. AgentState Task Methods

Add mutation methods to `AgentState` that encapsulate task lookup, validation, and state transitions. These are pure synchronous operations — no I/O, no events, no persistence.

**New methods on `AgentState`:**

```csharp
public AgentTaskInfo GetTask(AgentTaskId taskId)
```
Finds the task in `ActiveTasks` by ID. Throws `InvalidOperationException` if not found. Replaces the duplicated foreach+break pattern in `CompleteTaskAsync` (lines 267-278) and `ReviewTaskAsync` (lines 331-342).

```csharp
public AgentTaskInfo SubmitTask(string description)
```
Validates `RunningTaskCount < MaxConcurrentTasks`. Creates a new `AgentTaskInfo` with `AgentTaskId.New()`, adds it to `ActiveTasks`, sets `Status = Busy`, updates `LastActive`. Returns the task. Throws `InvalidOperationException` if at capacity. Replaces the body of `AgentGrain.SubmitTaskAsync` (lines 236-262).

```csharp
public void FailTask(AgentTaskId taskId, ProofOfWork proof)
```
Calls `GetTask`, sets `Status = Failed`, `CompletedAt = UtcNow`, attaches proof, calls `RefreshBusyStatus()`, updates `LastActive`. Replaces the failure branch of `CompleteTaskAsync` (lines 280-299).

```csharp
public void SetAwaitingReview(AgentTaskId taskId, ProofOfWork proof)
```
Calls `GetTask`, sets `Status = AwaitingReview`, attaches proof, updates `LastActive`. Replaces the success branch of `CompleteTaskAsync` (lines 302-305).

```csharp
public void AcceptTask(AgentTaskId taskId, string? feedback, VerificationRecord? verification)
```
Calls `GetTask`, validates `Status is AwaitingReview`, applies review metadata to proof (only if `Proof is not null` — matching current grain behavior), sets `Status = Accepted`, `CompletedAt = UtcNow`, increments `TotalTasksCompleted`, calls `RefreshBusyStatus()`, updates `LastActive`. Replaces the accepted branch of `ReviewTaskAsync` (lines 347-363).

```csharp
public void RejectTask(AgentTaskId taskId, string? feedback, VerificationRecord? verification)
```
Calls `GetTask`, validates `Status is AwaitingReview`, applies review metadata to proof (only if `Proof is not null`), sets `Status = Rejected`, calls `RefreshBusyStatus()`, updates `LastActive`. Replaces the rejected branch of `ReviewTaskAsync` (lines 347-367).

```csharp
public int RunningTaskCount { get; }
```
Computed property that counts tasks with `Status == Running`. Replaces the manual counting loop in `SubmitTaskAsync` (lines 239-244).

```csharp
public void RefreshBusyStatus()
```
Sets `Status = Active` if no tasks are running. Replaces `AgentGrain.UpdateAgentBusyStatus()` (lines 416-430).

**Shared validation helper (private):**

```csharp
private void ApplyReviewMetadata(AgentTaskInfo task, string? feedback, VerificationRecord? verification)
```
Guards `task.Proof is not null`, then applies `ReviewFeedback`, `ReviewedAt`, and `Verification` to the task's proof. Shared by `AcceptTask` and `RejectTask`. Replaces the common block in `ReviewTaskAsync` (lines 347-353).

**What changes in `AgentGrain`:**

`SubmitTaskAsync` shrinks from 32 lines to ~8:
```
validate active status → state.SubmitTask(description) → persist → log → return
```

`CompleteTaskAsync` shrinks from 62 lines to ~20:
```
state.FailTask or state.SetAwaitingReview → persist → publish event → log → (if success) fire verifier
```

`ReviewTaskAsync` shrinks from 55 lines to ~15:
```
state.AcceptTask or state.RejectTask → persist → publish event → log
```

`UpdateAgentBusyStatus` is deleted from the grain.

**Estimated reduction:** ~120 lines out of `AgentGrain`, ~80 lines added to `AgentState`.

### 2. AgentChatPipeline

Extract the chat pipeline into `AgentChatPipeline` behind `IAgentChatPipeline`. This is a regular (non-static) class that owns the `IChatClient` instance and system prompt cache.

**Interface:**

```csharp
// src/Assistants/Weave.Agents/Pipeline/IAgentChatPipeline.cs
public interface IAgentChatPipeline
{
    void Initialize(string agentId, string? model);
    void Reset();
    Task<AgentChatResponse> ExecuteAsync(AgentState state, AgentMessage message);
}
```

- `Initialize` — creates the `IChatClient` for the given agent/model. Called during `ActivateAgentAsync` and `OnActivateAsync`.
- `Reset` — clears `_chatClient` and `_systemPrompt` cache. Called during `ActivateAgentAsync` (before re-initialize) and `DeactivateAsync`.
- `ExecuteAsync` — the full chat flow: add user message to history → load system prompt → build chat messages → resolve tools → call chat client → convert response → update history. Mutates `state` directly. Returns `AgentChatResponse`.

**Implementation:**

```csharp
// src/Assistants/Weave.Agents/Pipeline/AgentChatPipeline.cs
public sealed class AgentChatPipeline(
    IGrainFactory grainFactory,
    IAgentChatClientFactory chatClientFactory) : IAgentChatPipeline
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
        // Full flow moved from AgentGrain.SendAsync lines 172-229
        // plus GetSystemPromptAsync, BuildToolsAsync, InvokeToolAsync
    }
}
```

**Private methods moved from `AgentGrain`:**
- `GetSystemPromptAsync(AgentState state)` — loads system prompt from file, caches result
- `BuildToolsAsync(AgentState state)` — resolves tools from `IToolRegistryGrain` via `grainFactory`
- `InvokeToolAsync(AgentState state, string toolName, string input)` — invokes a tool through the tool grain

All three take `AgentState` as parameter (for `WorkspaceId`, `AgentName`, `ConnectedTools`, `Definition`).

**What changes in `AgentGrain`:**

Constructor: replaces `IAgentChatClientFactory chatClientFactory` with `IAgentChatPipeline chatPipeline`. Removes `IGrainFactory grainFactory` only if no other method uses it. (It is still used by `CompleteTaskAsync` to get `IProofVerifierGrain`, so it stays.)

Removes fields: `_chatClient`, `_systemPrompt`.

`OnActivateAsync`: replaces `_chatClient = chatClientFactory.Create(...)` with `chatPipeline.Initialize(...)`.

`ActivateAgentAsync`: replaces `_chatClient = chatClientFactory.Create(...); _systemPrompt = null;` with `chatPipeline.Reset(); chatPipeline.Initialize(...)`.

`SendAsync` shrinks from 62 lines to ~8:
```
validate active status → chatPipeline.ExecuteAsync(state, message) → persist → return
```

**Estimated reduction:** ~110 lines out of `AgentGrain`, ~130 lines in `AgentChatPipeline`.

**Registration:** `AgentChatPipeline` is registered as transient in `Weave.Silo/Program.cs`:
```csharp
services.AddTransient<IAgentChatPipeline, AgentChatPipeline>();
```
Transient because each grain instance needs its own pipeline with its own `IChatClient` cache.

### 3. Silo Registration

Add to `src/Runtime/Weave.Silo/Program.cs`:
```csharp
services.AddTransient<IAgentChatPipeline, AgentChatPipeline>();
```

No other registration changes needed — `AgentGrain` is already resolved by Orleans, and `AgentState` is a model with no DI.

## What Stays in AgentGrain

After both extractions, `AgentGrain` (~280 lines) contains:

- **`OnActivateAsync`** (~12 lines) — read state, apply identity, initialize pipeline
- **`ActivateAgentAsync`** (~50 lines) — lifecycle hooks, state transitions, events, logging. The ceremony is the job.
- **`DeactivateAsync`** (~45 lines) — same ceremony pattern
- **`GetStateAsync`** (1 line)
- **`SendAsync`** (~8 lines) — validate, delegate to pipeline, persist
- **`SubmitTaskAsync`** (~8 lines) — validate, call state method, persist, log
- **`CompleteTaskAsync`** (~20 lines) — call state method, persist, publish event, log, fire verifier
- **`ReviewTaskAsync`** (~15 lines) — call state method, persist, publish event, log
- **`ConnectToolAsync` / `DisconnectToolAsync`** (~10 lines) — trivial, stay as-is
- **Identity helpers** (~70 lines) — `EnsureIdentity`, `ApplyIdentity`, `GetAgentName`, `TryGetPrimaryKeyString`. These are grain-specific and stay.

## Dependency Flow

No new project references required:

```
AgentState methods      → Weave.Agents (models, same file)
IAgentChatPipeline      → Weave.Agents (Pipeline/, new interface)
AgentChatPipeline       → Weave.Agents (Pipeline/, uses existing deps)
Registration            → Weave.Silo (Program.cs, one line)
```

`AgentChatPipeline` depends on `IGrainFactory` (Orleans), `IAgentChatClientFactory` (already in Pipeline/), `ChatMessageMapper` (already in Pipeline/), `ToolInvocationBuilder` (already referenced by Weave.Agents). No new external dependencies.

## Test Strategy

### AgentState Tests

New file: `src/Assistants/Weave.Agents.Tests/AgentStateTaskMethodTests.cs`

Tests are pure — no mocks, no async. Construct `AgentState`, call methods, assert state changes:

- `GetTask_ExistingId_ReturnsTask`
- `GetTask_UnknownId_Throws`
- `SubmitTask_UnderCapacity_ReturnsRunningTask`
- `SubmitTask_UnderCapacity_SetsBusyStatus`
- `SubmitTask_AtCapacity_Throws`
- `FailTask_SetsFailedStatus`
- `FailTask_RefreshesBusyStatus`
- `SetAwaitingReview_SetsStatusAndProof`
- `AcceptTask_SetsAcceptedAndCompletedAt`
- `AcceptTask_IncrementsTotalCompleted`
- `AcceptTask_WhenNotAwaitingReview_Throws`
- `AcceptTask_AppliesReviewMetadata`
- `RejectTask_SetsRejectedStatus`
- `RejectTask_WhenNotAwaitingReview_Throws`
- `RunningTaskCount_ReturnsCorrectCount`
- `RefreshBusyStatus_NoRunning_SetsActive`
- `RefreshBusyStatus_HasRunning_StaysBusy`

### AgentChatPipeline Tests

New file: `src/Assistants/Weave.Agents.Tests/AgentChatPipelineTests.cs`

Tests use mocked `IGrainFactory`, `IAgentChatClientFactory`, and `IChatClient`:

- `ExecuteAsync_AddsUserMessageToHistory`
- `ExecuteAsync_CallsChatClient`
- `ExecuteAsync_ReturnsResponseContent`
- `ExecuteAsync_WithConnectedTools_ResolvesFromRegistry`
- `ExecuteAsync_WithSystemPromptFile_LoadsPrompt`
- `ExecuteAsync_WithoutSystemPrompt_SkipsPrompt`
- `Initialize_CreatesChatClient`
- `Reset_ClearsCachedState`

### Existing AgentGrainTests

All 20 existing `AgentGrainTests` must continue to pass. They test through the grain's public API, so they validate the integration of state methods + pipeline + grain orchestration.

The `CreateGrain` helper in `AgentGrainTests` needs updating: replace `IAgentChatClientFactory` with `IAgentChatPipeline` in the constructor call.

## Out of Scope

- Changing `IAgentGrain` interface or any grain public API
- Splitting task management into a separate grain
- Extracting lifecycle hook ceremony into a reusable pattern
- Changing `ActivateAgentAsync` / `DeactivateAsync` structure (the ceremony *is* the work)
- Modifying any other grain or project
