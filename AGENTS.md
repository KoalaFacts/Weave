# Weave — Agent Architecture

## Overview

Weave agents are Orleans grains keyed by `{workspaceId}/{agentName}`. They own agent state, conversation history, active task tracking, tool connections, and the chat pipeline used to talk to an LLM provider.

This document describes the current implementation in the repository. It intentionally separates implemented behavior from future expansion ideas.

## Current Grain Topology

### `AgentGrain`

Primary responsibilities:

- activate an agent from a `WorkspaceId` and `AgentDefinition`
- keep `AgentState` persisted through Orleans storage
- append message history and call the `IChatClient` pipeline
- submit and complete agent tasks
- connect and disconnect tools
- publish lifecycle and task events

Current interface shape:

```csharp
public interface IAgentGrain : IGrainWithStringKey
{
    Task<AgentState> ActivateAgentAsync(WorkspaceId workspaceId, AgentDefinition definition);
    Task DeactivateAsync();
    Task<AgentState> GetStateAsync();
    Task<AgentChatResponse> SendAsync(AgentMessage message);
    Task<AgentTaskInfo> SubmitTaskAsync(string description);
    Task CompleteTaskAsync(AgentTaskId taskId, bool success, ProofOfWork proof);
    Task ReviewTaskAsync(AgentTaskId taskId, bool accepted, string? feedback = null, VerificationRecord? verification = null);
    Task ConnectToolAsync(string toolName);
    Task DisconnectToolAsync(string toolName);
}
```

### `AgentSupervisorGrain`

Keyed by `{workspaceId}`.

Current responsibilities:

- activate all agents from a workspace manifest
- deactivate all agents in a workspace
- return aggregate agent state snapshots

### `ToolRegistryGrain`

Keyed by `{workspaceId}`.

Current responsibilities:

- connect and track tools available within a workspace
- resolve tool connections for agent use
- return tool status to the API layer

### `HeartbeatGrain`

Keyed by `{workspaceId}/{agentName}`.

Current responsibilities:

- start and stop heartbeat scheduling for an agent
- track heartbeat state
- submit configured tasks on a cron-like schedule

### `ProofVerifierGrain`

Keyed by `{workspaceId}`. Acts as the consensus coordinator (analogous to a BTC mining pool).

Current responsibilities:

- store configurable verification conditions and validator count in persistent state
- dispatch proof to N independent `ProofValidatorGrain` instances in parallel
- collect votes and determine consensus (majority must accept)
- build a `VerificationRecord` audit trail with all individual votes
- call back into the originating agent grain with the consensus result
- publish `ProofVerifiedEvent` with vote counts

### `ProofValidatorGrain`

Keyed by `{workspaceId}/validator-{index}`.

Current responsibilities:

- independently evaluate proof items against plain-language verification conditions using an LLM (`IChatClient`)
- build a structured prompt containing the proof items and conditions, ask the AI to reason about each
- parse the AI response into per-condition `ConditionResult` entries
- return a `VerificationVote` with per-condition results and an overall accept/reject decision
- each validator runs autonomously with its own model configuration — like an independent BTC miner
- gracefully handle LLM failures (returns rejection with error detail rather than crashing)

## Agent Lifecycle

The current status model in `AgentState` is:

```text
Idle -> Activating -> Active -> Busy -> Active
  \-> Deactivating -> Idle
  \-> Error
```

Observed transitions:

- `ActivateAgentAsync(...)` moves an agent through `Activating` to `Active`
- `SendAsync(...)` requires `Active` or `Busy`
- `SubmitTaskAsync(...)` marks work as running and moves the agent to `Busy`
- `CompleteTaskAsync(...)` with proof moves the task to `AwaitingReview`; without proof it completes or fails directly
- `ReviewTaskAsync(...)` accepts or rejects a task that is awaiting review
- `DeactivateAsync()` clears active task and connected tool state, then returns to `Idle`

## Agent State

`AgentState` currently includes:

- `AgentId`
- `WorkspaceId`
- `AgentName`
- `Status`
- `Model`
- `ConnectedTools`
- `ActiveTasks`
- `MaxConcurrentTasks`
- `ActivatedAt` / `DeactivatedAt`
- `ErrorMessage`
- `History`
- `LastActive`
- `TotalTasksCompleted`
- `Definition`
- `ConversationId`

## LLM Pipeline

Agents use `Microsoft.Extensions.AI` through a small pipeline abstraction:

```text
AgentGrain -> CostTrackingChatClient -> RateLimitingChatClient -> provider client
```

Current pipeline components:

- `CostTrackingChatClient` records usage and cost data per agent
- `RateLimitingChatClient` enforces request throttling
- `AgentChatClientFactory` constructs the pipeline for a given agent and model

## Tool Invocation Model

The current tool flow is:

```text
AgentGrain -> ToolRegistryGrain -> ToolGrain -> IToolConnector
```

Implemented connector categories in the repo:

- MCP
- CLI
- OpenAPI
- Dapr (via `Weave.Plugin.Dapr`, loaded when the Dapr sidecar is detected)

`ToolGrain` currently performs:

- capability token validation
- outbound leak scanning before invocation
- secret substitution via `ISecretProxyGrain`
- connector dispatch
- inbound leak scanning and redaction on responses
- domain event publication for completed or blocked invocations

## Security Boundaries

Current security controls around agents and tools include:

- capability-token validation before tool access
- leak scanning on both outbound input and inbound output
- secret substitution through the secret proxy grain
- redaction when a tool response appears to contain secrets

## HTTP API Surface

`Weave.Silo` exposes agent endpoints under:

- `GET /api/workspaces/{workspaceId}/agents`
- `GET /api/workspaces/{workspaceId}/agents/{agentName}`
- `POST /api/workspaces/{workspaceId}/agents/{agentName}/activate`
- `POST /api/workspaces/{workspaceId}/agents/{agentName}/deactivate`
- `POST /api/workspaces/{workspaceId}/agents/{agentName}/messages`
- `POST /api/workspaces/{workspaceId}/agents/{agentName}/tasks`
- `POST /api/workspaces/{workspaceId}/agents/{agentName}/tasks/{taskId}/complete`
- `POST /api/workspaces/{workspaceId}/agents/{agentName}/tasks/{taskId}/review`

The CLI currently consumes workspace-level endpoints for start, stop, and status. Agent-specific CLI commands are not implemented yet.

## Proof of Work

Proof of work is mandatory. To complete a task successfully, an agent must provide verifiable evidence. Multiple independent validator grains evaluate the proof and must reach consensus before the task is accepted — similar to how BTC miners independently verify blocks.

### Task Status Flow

```text
Pending -> Running -> AwaitingReview (proof submitted) -> Accepted (consensus)
                                                       -> Rejected (consensus)
                   -> Failed (with proof of failure)
                   -> Cancelled
```

There is no direct `Running -> Completed` path. Every successful completion goes through consensus verification.

### Proof Model

`ProofOfWork` contains a list of `ProofItem` entries, each with:

- `Type` — one of `CiStatus`, `TestResults`, `PullRequest`, `CodeReview`, `DiffSummary`, `Custom`
- `Label` — short human-readable label
- `Value` — the evidence value (status string, count, PR number, etc.)
- `Uri` — optional link to the evidence source

After review, `ProofOfWork` records `ReviewFeedback`, `ReviewedAt`, and a `VerificationRecord` containing the full audit trail.

### Verification Conditions

Conditions are plain-language descriptions that AI validator agents can reason about. Each `VerificationCondition` specifies:

- `Name` — unique condition identifier
- `Description` — plain-language description of what the condition requires, written so an AI agent can evaluate it

Conditions are configurable per workspace via `ProofVerifierGrain.ConfigureAsync`.

Default conditions (used when none are configured):

- `ci-passing` — "The CI/build status must indicate that the build passed successfully."
- `tests-passing` — "Test results must not indicate any failures."
- `pr-has-link` — "If a pull request is referenced, it must include a URI link."
- `code-review-present` — "Code review evidence must be present with a meaningful value."
- `diff-present` — "A diff summary must be present showing what code changes were made."
- `custom-present` — "Any custom proof items must have meaningful, non-empty values."

All proof items must also have non-empty values regardless of conditions.

### Consensus Model

The `ProofVerifierGrain` dispatches proof to N independent `ProofValidatorGrain` instances (default 2, configurable per workspace). Each validator is an AI agent that independently reasons about the proof using an LLM.

Users can configure:

- **Validator count** — how many independent AI validators evaluate the proof (default 2)
- **Validator models** — each validator can use a different LLM model via `ValidatorConfig`, enabling mixed-model consensus (e.g., validator-0 uses GPT-4o, validator-1 uses Claude). When fewer model configs than validators are specified, remaining validators use the system default model.

Consensus requires a strict majority: more than half the validators must accept. For example, with 2 validators the threshold is 2 (both must agree); with 3 it is 2; with 5 it is 3.

### Feedback Loop

The feedback loop is built into the system's DNA. When validators reject a task:

1. Each validator provides per-condition reasoning explaining why the proof fails
2. The rejection feedback aggregates these reasons and is delivered to the agent via `ReviewTaskAsync`
3. The agent receives the detailed `VerificationRecord` with every validator's vote and condition results
4. The agent can use this feedback to understand exactly what needs to be fixed
5. The agent reworks the proof and resubmits via `CompleteTaskAsync`
6. The cycle repeats until consensus is reached

This creates a self-correcting loop where agents improve their work based on specific, actionable feedback from independent AI reviewers.

### Audit Trail

Every verification produces a `VerificationRecord` stored on the task's `ProofOfWork`. It contains:

- `Votes` — each validator's `VerificationVote` with per-condition `ConditionResult` details
- `RequiredVotes` — the majority threshold
- `ConsensusReached` — whether a clear majority was reached
- `Accepted` — the final consensus decision
- `CompletedAt` — when verification completed

This record is persisted with the task and exposed through the API for full audit visibility.

### Flow

1. Agent calls `CompleteTaskAsync(taskId, success: true, proof)` with evidence attached
2. Task transitions to `AwaitingReview` and publishes `AgentTaskAwaitingReviewEvent`
3. `AgentGrain` dispatches to `ProofVerifierGrain` (fire-and-forget)
4. Verifier loads conditions and dispatches to N `ProofValidatorGrain` instances in parallel
5. Each validator independently evaluates all conditions using its configured LLM, returns a `VerificationVote`
6. Verifier collects votes and checks consensus (majority threshold)
7. Verifier calls `ReviewTaskAsync` on the agent grain with the result and `VerificationRecord`
8. If accepted: task moves to `Accepted`, `TotalTasksCompleted` increments
9. If rejected: task moves to `Rejected`, agent can rework and resubmit
10. `AgentTaskReviewedEvent` and `ProofVerifiedEvent` (with vote counts) are published

When `success: false`, the task fails immediately with the proof recorded for diagnostics — no consensus verification is triggered.

## Manifest Configuration

Agents are declared in `workspace.json` through `AgentDefinition`:

```jsonc
{
  "version": "1.0",
  "name": "my-workspace",
  "agents": {
    "assistant": {
      "model": "claude-sonnet-4-20250514",
      "system_prompt_file": "./prompts/assistant.md",
      "max_concurrent_tasks": 3,
      "tools": ["git"],
      "capabilities": ["tool:*"],
      "heartbeat": {
        "cron": "0 * * * *",
        "tasks": ["Review repository status"]
      }
    }
  }
}
```

The manifest uses JSONC (JSON with comments and trailing commas) so hand-edited files can include inline documentation.

Supported manifest fields today include:

- `model`
- `system_prompt_file`
- `max_concurrent_tasks`
- `memory`
- `tools`
- `capabilities`
- `heartbeat`
- `target`

## Runtime Modes

The Silo supports two runtime modes, selected automatically at startup.

### Local Mode

Detected when `Weave:LocalMode` is `true` or `Orleans:ClusterId` is not set (i.e., not running under Aspire).

Configuration:

- Orleans uses `UseLocalhostClustering()` — single-node, no external coordination
- Grain storage uses `AddMemoryGrainStorageAsDefault()` — state lives in memory for the process lifetime
- `InProcessRuntime` is registered — all container and network operations are no-ops
- No Redis, Podman, Dapr, or Aspire required

Starting locally:

```bash
dotnet run --project src/Runtime/Weave.Silo -- --Weave:LocalMode=true
```

Or via the CLI:

```bash
weave serve
```

The `weave serve` command finds the Silo binary (via `WEAVE_SILO_PATH` env, well-known dev paths, or adjacent published binary), starts it with local mode config on `http://localhost:9401`, and waits for it to become healthy.

The CLI `weave workspace up` command checks reachability before starting a workspace and directs the user to `weave serve` if the Silo is not running.

### Distributed Mode

Active when running under Aspire (`Orleans:ClusterId` is set) or when `Weave:LocalMode` is `false` with explicit Orleans config.

Configuration:

- Orleans clustering and persistence via Redis (or future Valkey/Garnet)
- `PodmanRuntime` manages real containers for tools
- Dapr sidecar for pub/sub when the `Weave.Plugin.Dapr` plugin detects a running sidecar
- Full Aspire service defaults (OTEL, service discovery)

### `InProcessRuntime`

Implements `IWorkspaceRuntime` for local mode. Behavior:

- `ProvisionAsync` — returns a `WorkspaceEnvironment` with `NetworkId.From("local")` and no containers
- `StartContainerAsync` — returns a local handle (`local-{name}`) without starting anything
- `TeardownAsync`, `StopContainerAsync`, `CreateNetworkAsync`, `DeleteNetworkAsync` — no-ops
- `RuntimeName` is `"in-process"`

This allows workspace grains to complete their full lifecycle without container infrastructure.

### Storage Spectrum (Planned)

| Tier | Storage | Use Case |
|------|---------|----------|
| Ephemeral | Memory (current local default) | Development, quick experiments |
| Local | SQLite via ADO.NET | Persistent local workspaces |
| Distributed | Valkey / Garnet | Production multi-node clusters |

Redis is avoided for new distributed work due to the RSALv2/SSPL licensing change (March 2024). Valkey (BSD, Linux Foundation) is the planned replacement.

## Current Limitations

The repository does not currently implement:

- remote agent registration flows
- ephemeral runner-style agents
- label-based remote scheduling
- dedicated CLI commands for direct agent chat or task submission

Those ideas can still inform roadmap planning, but they should not be documented as shipped behavior.

## Near-Term Direction

The current codebase is strongest around a hosted, workspace-centric flow:

1. create a workspace manifest
2. start the workspace through `Weave.Silo`
3. activate agents from the manifest
4. connect tools through the registry
5. inspect state through the API or dashboard

That hosted flow should remain the baseline for the MVP. Local mode (no external services) is the default for first-time users. Distributed mode via Aspire is available for production deployments.
