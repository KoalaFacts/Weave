# Task Endpoints + Status Filtering — DX Improvement (Phase 2)

**Date:** 2026-04-06
**Status:** Approved
**Scope:** Add GET endpoints for listing and fetching individual agent tasks, with optional status filtering on the list endpoint.

## Problem

The current API allows submitting, completing, and reviewing tasks through the agent, but there's no way to:
- List tasks for an agent (optionally filtered by status)
- Get a single task by ID

Tasks are embedded inside `AgentResponse.ActiveTasks`, forcing clients to fetch the full agent state to find a specific task or filter by status. The review workflow needs a "show me tasks awaiting review" query.

## Design

### New Endpoints

```
GET /api/workspaces/{workspaceId}/agents/{agentName}/tasks[?status=awaitingReview]
GET /api/workspaces/{workspaceId}/agents/{agentName}/tasks/{taskId}
```

### List Tasks

`GET .../tasks` returns all tasks for the agent. Accepts an optional `status` query parameter to filter by `AgentTaskStatus`.

- Status parameter uses camelCase enum values: `running`, `awaitingReview`, `accepted`, `rejected`, `failed`, `pending`, `completed`, `cancelled`.
- Without `?status`, returns all tasks.
- Invalid status value → 400 `ValidationProblemDetails` with error on `status` field.
- Agent not found (empty `AgentId`) → 404 `ProblemDetails`.
- Returns `List<TaskResponse>` (may be empty).

### Get Single Task

`GET .../tasks/{taskId}` returns a single task by ID.

- Task not found in `ActiveTasks` → 404 `ProblemDetails`.
- Agent not found (empty `AgentId`) → 404 `ProblemDetails`.
- Returns `TaskResponse`.

### Implementation Approach

Both endpoints reuse the existing `GetAgentStateQuery` to fetch `AgentState`, then project from `ActiveTasks`. No new CQRS queries, no grain changes. This is consistent with approach A (query full state, project at API layer).

Status parsing uses `Enum.TryParse<AgentTaskStatus>(statusParam, ignoreCase: true, out var parsed)` to handle the camelCase input.

### What Changes

| Action | File | Change |
|--------|------|--------|
| Modify | `src/Runtime/Weave.Silo/Api/AgentEndpoints.cs` | Add `GetTasks` and `GetTask` methods, register 2 new routes |

No new files. No domain, grain, or CQRS changes.

## Out of Scope

- Pagination (skip/take) — lists are naturally small (bounded by MaxConcurrentTasks)
- Task history beyond ActiveTasks — completed tasks that were cleared from state
- Filtering on other endpoints (agents, tools, workspaces)
