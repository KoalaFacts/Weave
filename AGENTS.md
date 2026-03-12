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
    Task CompleteTaskAsync(AgentTaskId taskId, bool success);
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
- `CompleteTaskAsync(...)` returns the agent to `Active` when no tasks remain running
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
- Dapr, when Dapr is configured in the Silo host

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

The CLI currently consumes workspace-level endpoints for start, stop, and status. Agent-specific CLI commands are not implemented yet.

## Manifest Configuration

Agents are declared in `workspace.yml` through `AgentDefinition`:

```yaml
agents:
  assistant:
    model: claude-sonnet-4-20250514
    system_prompt_file: ./prompts/assistant.md
    max_concurrent_tasks: 3
    tools: [git]
    capabilities: [tool:*]
    heartbeat:
      cron: "0 * * * *"
      tasks:
        - Review repository status
```

Supported manifest fields today include:

- `model`
- `system_prompt_file`
- `max_concurrent_tasks`
- `memory`
- `tools`
- `capabilities`
- `heartbeat`
- `target`

## Current Limitations

The repository does not currently implement:

- remote agent registration flows
- ephemeral runner-style agents
- label-based remote scheduling
- a separate in-process local agent orchestrator
- dedicated CLI commands for direct agent chat or task submission

Those ideas can still inform roadmap planning, but they should not be documented as shipped behavior.

## Near-Term Direction

The current codebase is strongest around a hosted, workspace-centric flow:

1. create a workspace manifest
2. start the workspace through `Weave.Silo`
3. activate agents from the manifest
4. connect tools through the registry
5. inspect state through the API or dashboard

That hosted flow should remain the baseline for the MVP.
