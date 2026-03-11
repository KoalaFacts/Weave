# Weave — Agent Architecture

## Overview

Weave agents are Orleans virtual actors (grains) that own the AI reasoning loop. Each agent is a long-lived, single-threaded, location-transparent entity that can be distributed across a cluster or run in-process for local development.

## Agent Lifecycle

```
                ┌──────────┐
                │  Created  │
                └─────┬─────┘
                      │ ActivateAgentAsync()
                      ▼
                ┌──────────┐
       ┌───────│  Active   │◄──────────────┐
       │       └─────┬─────┘               │
       │             │                      │
       │   SendAsync() / SubmitTaskAsync()  │ Resume
       │             │                      │
       │       ┌─────▼─────┐               │
       │       │ Processing │───────────────┘
       │       └─────┬─────┘
       │             │
       │    Error / Deactivate
       │             │
       │       ┌─────▼─────┐
       │       │  Errored   │
       │       └─────┬─────┘
       │             │
       └─────────────┘
              Deactivate
                 │
           ┌─────▼──────┐
           │ Deactivated │
           └─────────────┘
```

## Grain Architecture

### AgentGrain

**Key:** `{workspaceId}/{agentName}` (string)

The `AgentGrain` is the core AI reasoning unit. It:
1. Maintains conversation history and agent state
2. Dispatches LLM calls through the `IChatClient` middleware pipeline
3. Invokes tools via the `ToolRegistryGrain`
4. Manages concurrent task limits
5. Publishes domain events on state transitions

```csharp
interface IAgentGrain : IGrainWithStringKey
{
    Task<AgentState> ActivateAgentAsync(AgentConfig config);
    Task<AgentResponse> SendAsync(AgentMessage message);
    Task SubmitTaskAsync(string description);
    Task<AgentState> GetStateAsync();
    Task DeactivateAgentAsync();
}
```

### AgentSupervisorGrain

**Key:** `{workspaceId}` (string)

Manages the fleet of agents within a workspace:
- Activates/deactivates agents based on the workspace manifest
- Routes messages to the correct agent
- Enforces workspace-level agent limits
- Reports aggregate agent status

### ToolRegistryGrain

**Key:** `{workspaceId}` (string)

Central registry of tools available to agents within a workspace:
- Registers and deregisters tools
- Resolves tool names to `ToolGrain` references
- Enforces per-agent tool access (allow/deny lists)

### HeartbeatGrain

**Key:** `{workspaceId}/{agentName}` (mirrors agent key)

Proactive agent behavior inspired by OpenClaw's heartbeat system:
- Runs on a cron schedule (e.g., `*/30 * * * *` = every 30 minutes)
- Wakes the agent and submits predefined tasks from the heartbeat config
- Respects agent capacity — defers tasks if agent is at max concurrency
- Tracks execution count and last/next run times

```yaml
# In workspace.yml
agents:
  researcher:
    heartbeat:
      cron: "*/30 * * * *"
      tasks:
        - Check for new research papers on topic X
        - Summarize unread emails
```

## LLM Pipeline

All LLM calls flow through the `IChatClient` middleware pipeline (Microsoft.Extensions.AI):

```
Agent → CostTrackingChatClient → RateLimitingChatClient → [Provider Client]
         (tracks tokens/cost)     (token bucket limiter)    (OpenAI, Anthropic, etc.)
```

### CostTrackingChatClient

Tracks per-agent token usage and estimated cost:
- Input/output token counts per request
- Cumulative totals per agent ID
- Model ID tracking for cost estimation
- Queryable via `GetCostSummary(agentId)` and `GetAllCosts()`

### RateLimitingChatClient

Token bucket rate limiter per pipeline instance:
- Configurable requests per minute
- Queue depth limit (default: 10, oldest-first processing)
- Throws `InvalidOperationException` when rate exceeded

## Tool Invocation Flow

```
1. Agent decides to call a tool (LLM function calling)
2. AgentGrain → ToolRegistryGrain.ResolveAsync(toolName)
3. AgentGrain → ToolGrain.InvokeAsync(invocation, capabilityToken)
4. ToolGrain validates capability token
5. ToolGrain runs LeakScanner on request payload
6. ToolGrain delegates to IToolConnector (MCP/Dapr/CLI/OpenAPI)
7. ToolGrain runs LeakScanner on response payload
8. Result returned to AgentGrain
9. Agent continues LLM loop with tool result
```

### Security at Every Boundary

- **Capability tokens** are validated at both the agent and tool grain boundaries (defense in depth)
- **Leak scanning** runs on both inbound (request) and outbound (response) payloads
- **Secret placeholders** (`{secret:X}`) are substituted only at the network boundary via the transparent proxy
- **No grain ever sees raw secret values** — only encrypted `SecretValue` or placeholder references

## Inter-Agent Communication

Agents within the same workspace can communicate via:

1. **Direct messaging** — `AgentGrain.SendAsync()` with a message referencing another agent
2. **Task delegation** — `AgentGrain.SubmitTaskAsync()` with routing to a target agent
3. **Domain events** — publish events that other agents subscribe to via `IEventBus`
4. **Shared tool state** — agents in the same workspace share the `ToolRegistryGrain`

## Agent State Model

```csharp
[GenerateSerializer]
public sealed record AgentState
{
    [Id(0)] public AgentStatus Status { get; set; } = AgentStatus.Created;
    [Id(1)] public AgentConfig Config { get; set; } = new();
    [Id(2)] public List<AgentTask> ActiveTasks { get; set; } = [];
    [Id(3)] public List<ConversationMessage> History { get; set; } = [];
    [Id(4)] public DateTimeOffset? LastActive { get; set; }
    [Id(5)] public int TotalTasksCompleted { get; set; }
}

public enum AgentStatus { Created, Active, Processing, Errored, Deactivated }
```

## Configuration

Agents are configured in `workspace.yml`:

```yaml
agents:
  researcher:
    model: claude-sonnet-4-20250514         # LLM model ID
    system_prompt_file: ./prompts/researcher.md  # System prompt path
    max_concurrent_tasks: 5                  # Task concurrency limit
    memory:
      provider: redis                        # redis | sqlite | in-memory
      ttl: 24h                               # Conversation history TTL
    tools: [web-search, github-api]          # Allowed tools
    capabilities: [net:outbound, fs:read:/workspace/data]  # Sandbox permissions
    heartbeat:                               # Proactive behavior
      cron: "*/30 * * * *"
      tasks:
        - Check for new research papers
```

## Agent Registration (Remote Agents)

For distributed deployments, agents can register with the Weave platform using a token-based flow (GitHub runner-style):

```bash
# 1. Generate a registration token
weave agent register --workspace=my-workspace --generate-token
# → Token: WEAVE-REG-abc123def456 (expires in 1 hour)

# 2. Configure the agent on a remote machine
weave agent configure \
    --url https://weave.example.com \
    --token WEAVE-REG-abc123def456 \
    --name "gpu-agent-01" \
    --labels "gpu,cuda,a100"

# 3. Start the agent
weave agent run
# → Listening for work from workspace "my-workspace"...
```

**Key features:**
- **One-time registration tokens** — short-lived (1h default), exchanged for long-lived capability tokens
- **Labels** — agents self-declare capabilities (`gpu`, `region:us-east`, `tool:browser`). Workspaces route tasks by label selectors
- **Ephemeral mode** — `--ephemeral` flag deregisters after one task (like GitHub ephemeral runners)
- **Graceful drain** — `weave agent drain` stops accepting new work, finishes current tasks, exits

## Scaling

### Local Mode (AOT)

- `InProcessOrchestrator` manages agents via `Task`-based concurrency + `Channel<T>` message passing
- No Orleans dependency — fully AOT-publishable
- One workspace per process; multiple workspaces = multiple CLI processes

### Cluster Mode (Orleans)

- Standard Orleans silo with Redis clustering
- Agents distributed across silos via Orleans placement
- Heartbeats use Orleans grain timers (survive silo restarts)
- Inter-agent messaging via Orleans grain references (location-transparent)

### Workspace Isolation

Each workspace gets:
- Its own network namespace (container isolation)
- Separate capability token scope
- Independent secret mount
- Isolated filesystem root
- Agents in workspace A cannot see workspace B's grains
