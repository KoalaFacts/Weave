# Weave Documentation Index

> AI-optimized documentation for the Weave AI assistant orchestration platform.

## System Overview

Weave is a distributed AI agent orchestration platform built on .NET, Microsoft Orleans (actor model), and .NET Aspire. It manages workspaces containing AI agents that use tools, with security, lifecycle, and deployment capabilities.

## Dependency Graph

```
Weave.Shared (Foundation)
    ↓
Weave.SourceGen (Foundation, compile-time)
    ↓
Weave.Workspaces
    ↓
┌───────────────┬───────────────┬───────────────┐
│ Weave.Agents  │ Weave.Tools   │ Weave.Security│
│ (Assistants)  │               │               │
└───────┬───────┴───────┬───────┴───────┬───────┘
        │               │               │
        └───────┬───────┘               │
                ↓                       │
        Weave.Deploy                    │
                ↓                       │
┌───────────────────────────────────────┘
↓
Weave.Silo (Runtime — composes everything)
    ↓
┌───────────────┬───────────────┐
│ Weave.Cli     │ Weave.Dashboard│
│ (UX)          │ (UX)           │
└───────────────┴───────────────┘
    ↓
Weave.AppHost (Aspire orchestrator)
```

## Subsystem Documentation

| Document | Subsystem | Source Path | Key Concepts |
|----------|-----------|-------------|--------------|
| [foundation.md](foundation.md) | Shared + SourceGen | `src/Foundation/` | Branded IDs, CQRS dispatch, domain events, lifecycle hooks, SecretValue, source generators |
| [workspaces.md](workspaces.md) | Workspaces | `src/Workspaces/` | Workspace manifest (JSONC), manifest parser/validator, runtime abstraction (InProcess/Podman), workspace actor, plugin registry |
| [assistants.md](assistants.md) | Agents | `src/Assistants/` | Agent actor, supervisor, heartbeat scheduling, chat pipeline (rate limiting, cost tracking), task management, proof-of-work verification |
| [tools.md](tools.md) | Tools | `src/Tools/` | Tool connectors (MCP, CLI, OpenAPI, Dapr, DirectHTTP), tool actor with security scanning, tool discovery service |
| [security.md](security.md) | Security | `src/Security/` | Capability tokens (HMAC-SHA256), leak scanner (15 patterns + entropy), secret proxy, Vault/InMemory secret providers |
| [deployment.md](deployment.md) | Deployment | `src/Deployment/` | Publishers: Docker Compose, Kubernetes, Nomad, Fly.io, GitHub Actions |
| [runtime.md](runtime.md) | Runtime | `src/Runtime/` | Orleans Silo host, Aspire AppHost, plugin wiring (Dapr/Vault), REST API, CQRS handler registration |
| [ux.md](ux.md) | CLI + Dashboard | `src/UX/` | CLI commands (System.CommandLine + Spectre.Console), Blazor dashboard (FluentUI), workspace presets |
| [examples.md](examples.md) | Code Examples | (cross-cutting) | Manifest authoring, CLI usage, actors, CQRS handlers, tokens, events, lifecycle hooks, testing patterns |

## Strategy & Direction

| Document | Purpose |
|----------|---------|
| [unique-agent-strategy.md](unique-agent-strategy.md) | Capability-first positioning, what we compete on, what we do not, and roadmap implications |
| [competitive-analysis.md](competitive-analysis.md) | Market context: Hermes Agent, OpenClaw, Evlover. Recommendations sections superseded by `unique-agent-strategy.md` |

## Cross-Subsystem Interactions

### Workspace Startup Flow
`CLI/Dashboard` → `POST /api/workspaces` → `Silo (CQRS)` → `WorkspaceActor.StartAsync()` → `Runtime.ProvisionAsync()` → `ToolRegistryActor.ConnectToolsAsync()` → `AgentSupervisorActor.ActivateAllAsync()` → `HeartbeatActor.StartAsync()`

### Tool Invocation Security Flow
`AgentActor.SendAsync()` → `ToolRegistryActor.ResolveAsync()` → validate `CapabilityToken` → `SecretProxyActor.SubstituteAsync()` → `LeakScanner` (outbound) → `ToolConnector.InvokeAsync()` → `LeakScanner` (inbound) → result

### Plugin Hot-Swap Flow
`POST /api/plugins` → `PluginRegistry.ConnectAsync()` → `PluginConnector.ConnectAsync()` → `PluginServiceBroker.Swap<T>()` → `EventBusProxy` replays subscriptions

### Secret Resolution Flow
`{secret:path}` placeholder → `SecretProxyActor.SubstituteAsync()` → `ISecretProvider.ResolveAsync()` (Vault or InMemory via `SecretProviderProxy`) → `TransparentSecretProxy.SubstitutePlaceholders()` → actual value injected

## Key Patterns

| Pattern | Where Used | Purpose |
|---------|-----------|---------|
| Orleans Actors | Agents, Tools, Workspaces, Security | Distributed stateful actors |
| CQRS | Foundation → Silo | Command/query separation with source-generated handlers |
| Domain Events | All subsystems | Pub/sub via EventBus (InProcess, Dapr, or Webhook) |
| Lifecycle Hooks | Workspaces, Tools, Agents | Extensible pre/post phase callbacks |
| Capability Tokens | Security → Tools | HMAC-signed tokens gating tool/secret access |
| Plugin Hot-Swap | Runtime, Foundation | PluginServiceBroker enables runtime service replacement |
| Source Generation | Foundation | Zero-reflection branded IDs and CQRS registration |

## Actor Key Reference

| Actor | Key Format | Example |
|-------|-----------|---------|
| WorkspaceActor | `{workspaceId}` | `ws-abc123` |
| WorkspaceRegistryActor | `"active"` (singleton) | `active` |
| AgentActor | `{workspaceId}/{agentName}` | `ws-abc123/researcher` |
| AgentSupervisorActor | `{workspaceId}` | `ws-abc123` |
| HeartbeatActor | `{workspaceId}/{agentName}` | `ws-abc123/researcher` |
| ToolActor | `{workspaceId}/{toolName}` | `ws-abc123/web-search` |
| ToolRegistryActor | `{workspaceId}` | `ws-abc123` |
| ProofVerifierActor | `{workspaceId}` | `ws-abc123` |
| ProofValidatorActor | `{workspaceId}/validator-{n}` | `ws-abc123/validator-0` |
| SecretProxyActor | `{workspaceId}` | `ws-abc123` |

## API Surface

All REST endpoints are on the Silo at default port 5000 (CLI uses 9401).

| Group | Base Path | Key Operations |
|-------|-----------|---------------|
| Workspaces | `/api/workspaces` | CRUD + start/stop |
| Agents | `/api/workspaces/{id}/agents` | Activate, message, tasks |
| Tools | `/api/workspaces/{id}/tools` | List connections |
| Plugins | `/api/plugins` | Connect/disconnect, catalog |
| Health | `/health`, `/alive` | Liveness checks |

## Technology Stack

| Layer | Technology |
|-------|-----------|
| Runtime | .NET 10, C# |
| Actor Framework | Microsoft Orleans |
| Orchestration | .NET Aspire |
| State Store | Redis |
| Event Bus | In-process / Dapr / Webhook |
| Secrets | In-memory / HashiCorp Vault |
| CLI UI | System.CommandLine + Spectre.Console |
| Web UI | Blazor Server + FluentUI |
| Testing | xUnit v3, Shouldly, NSubstitute |
| Serialization | System.Text.Json (source-generated) |
| Observability | OpenTelemetry |
