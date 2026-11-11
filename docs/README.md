# Weave documentation index

> Entry point to the documentation: where current rules live, and which older guides are historical.

## Where to start

| You want | Read |
|----------|------|
| What Weave is and how to try it | [README.md](../README.md) |
| Shared development rules, current project layout and verification | [AGENTS.md](../AGENTS.md) |
| Target semantics (agent network, Governed Tools) | [ARCHITECTURE.md](../ARCHITECTURE.md) |
| What each change delivered and how it was verified | [Implementation records](implementation/README.md) |
| The mailbox and publishing Workers (demo) | [typescript/mailbox-worker](../typescript/mailbox-worker/README.md), [typescript/auth-worker](../typescript/auth-worker/README.md) |
| Repository rules with examples | [best-practices.md](best-practices.md) |
| Use Codex with local documents through the published CLI | [local-codex.md](local-codex.md) |
| Write a workspace manifest | [manifest-reference.md](manifest-reference.md) |
| The runtime-neutral contract both implementations target (target design) | [runtime-contract.md](runtime-contract.md) |
| Product positioning, boundary with sibling products and current validation limits (Chinese) | [product-positioning.md](product-positioning.md) |
| Releases, coverage and browser tests | [releasing.md](releasing.md), [coverage.md](coverage.md), [browser-testing.md](browser-testing.md) |

## Current source layout

The C#/.NET code is flat by feature: product features are direct children of `csharp/src/` (for example `Agents`,
`Authority`, `Contacts`, `Invocations`, `Mailboxes`, `Plugins`, `Resources`, `Workspaces`), hosts compose them in
`csharp/hosts/`, and opt-in provider and runtime dependencies live in `csharp/extensions/`. The project table and the
rules for adding code are in [AGENTS.md](../AGENTS.md#ownership-and-composition). TypeScript packages, including the demo
Workers, are under `typescript/`.

## Historical subsystem guides

These guides were written for the earlier layer-first layout (`Weave.Shared`, `csharp/src/Foundation/`,
`csharp/src/Tools/` and similar), which the flat layout replaced, and many of their source paths no longer exist. They
still explain concepts and security rules that apply; for current behaviour use the code, AGENTS.md and the
implementation records. The sections after the strategy table in this file date from the same period.

| Document | Subsystem | Key concepts |
|----------|-----------|--------------|
| [foundation.md](foundation.md) | Shared + SourceGen | Branded IDs, CQRS dispatch, domain events, lifecycle hooks, SecretValue, source generators |
| [workspaces.md](workspaces.md) | Workspaces | Workspace manifest (JSONC), parser/validator, runtime abstraction, workspace actor, plugin registry |
| [assistants.md](assistants.md) | Agents | Agent actor, supervisor, heartbeat, chat pipeline, task management, proof-of-work verification |
| [tools.md](tools.md) | Tools | Tool connectors (MCP, CLI, OpenAPI, Dapr, DirectHTTP), tool actor with security scanning |
| [security.md](security.md) | Security | Capability tokens, leak scanner, secret proxy, secret providers |
| [deployment.md](deployment.md) | Deployment | Publishers: Docker Compose, Kubernetes, Nomad, Fly.io, GitHub Actions |
| [runtime.md](runtime.md) | Runtime | Orleans silo host, Aspire AppHost, plugin wiring, REST API |
| [ux.md](ux.md) | CLI + Dashboard | CLI commands, Blazor dashboard, workspace presets |
| [handoff.md](handoff.md) | Action layer and session log | Frontend-agnostic actions, capability vocabulary, audit trail |
| [examples.md](examples.md) | Code examples | Manifest authoring, CLI usage, actors, CQRS handlers, tokens, events, testing patterns |

## Strategy and direction

[product-positioning.md](product-positioning.md) is the only current positioning document: the two primitives, the boundary with AI Office and Envoylet, and the implementation focus. The earlier capability-first runtime strategy and competitor analysis were removed on 2026-11-10 because they described a product Weave no longer claims to be; they remain in Git history.

## Cross-Subsystem Interactions (historical)

### Workspace Startup Flow
`CLI/Dashboard` → `POST /api/workspaces` → `Silo (CQRS)` → `WorkspaceActor.StartAsync()` → `Runtime.ProvisionAsync()` → `ToolRegistryActor.ConnectToolsAsync()` → `AgentSupervisorActor.ActivateAllAsync()` → `HeartbeatActor.StartAsync()`

### Tool Invocation Security Flow
`AgentActor.SendAsync()` → `ToolRegistryActor.ResolveAsync()` → validate `CapabilityToken` → `SecretProxyActor.SubstituteAsync()` → `LeakScanner` (outbound) → `ToolConnector.InvokeAsync()` → `LeakScanner` (inbound) → result

### Plugin Hot-Swap Flow
`POST /api/plugins` → `PluginRegistry.ConnectAsync()` → `PluginConnector.ConnectAsync()` → `PluginServiceBroker.Swap<T>()` → `EventBusProxy` replays subscriptions

### Secret Resolution Flow
`{secret:path}` placeholder → `SecretProxyActor.SubstituteAsync()` → `ISecretProvider.ResolveAsync()` (Vault or InMemory via `SecretProviderProxy`) → `TransparentSecretProxy.SubstitutePlaceholders()` → actual value injected

### Frontend Action Flow (Shape C, Phase 1)
`weave agents <ws>` (CLI) / `/agents` (TUI) → `AgentsCliCommand` or `TuiAgentListView` → `ListAgentsAction.ExecuteAsync()` → typed `HttpClient` → `GET /api/workspaces/{id}/agents` (silo) → CQRS `GetAgentsQuery` → `IAgentActorGrain.GetStatusAsync()` → `AgentState` → `ApiAgentResponse[]` JSON → `AgentWire[]` (deserialized via `AgentJsonContext`) → `AgentSummary[]` (curated) → `ActionResult<ListAgentsResult>` → shared `AgentsRenderer` (Spectre table)

## Frontend Action Layer (Shape C)

The action layer is the shared seam between the silo HTTP API and the frontends (CLI, TUI, future Web UI). Each action is a self-contained verb: takes a typed `HttpClient` (BaseAddress configured by the frontend's DI registration), calls one or two silo endpoints, returns an `ActionResult<T>` carrying a curated frontend DTO. No shared "silo client" abstraction; each action owns its HTTP and translates wire shapes locally.

```
┌────────────────────────────────────────────────────────────────────┐
│                     User-facing surfaces                           │
│   ┌─────────────┐   ┌─────────────┐   ┌─────────────┐              │
│   │  CLI commands│  │  TUI slashes│   │  Dashboard  │              │
│   │  weave system│  │  /system    │   │  /agents    │              │
│   │  weave agents│  │  /agents    │   │   ...       │              │
│   └──────┬──────┘   └──────┬──────┘   └──────┬──────┘              │
└──────────│─────────────────│─────────────────│────────────────────┘
           │                 │                 │
           └────────┬────────┴────────┬────────┘
                    ▼                 ▼
       ┌────────────────────────────────────────────┐
       │            Weave.Actions                   │
       │                                            │
       │   Context/   IActionPrompter,              │
       │              IActionReporter,              │
       │              ActionResult<T>,              │
       │              ActionFailure                 │
       │                                            │
       │   <Verb>/    Action class + Input/Result   │
       │              + per-feature wire DTO        │
       │              + per-feature JsonContext     │
       │                                            │
       │   AddSiloActions(c => c.BaseAddress=...)   │
       │   ─── typed HttpClient per action via DI ──│
       └────────────────────│───────────────────────┘
                            │  HTTP (one call per action)
                            ▼
                  ┌──────────────────────┐
                  │      Weave.Silo      │
                  │  HTTP → CQRS → grain │
                  │       → state        │
                  └──────────────────────┘
```

| Piece | Shape | Lives in |
|---|---|---|
| `IActionPrompter` / `IActionReporter` | frontend-supplied interfaces (Spectre / Blazor / test fakes) | `Weave.Actions/Context/` |
| `ActionResult<T>` + `ActionFailure` | typed success-or-failure envelope; reasons enumerated for stable UX branching | `Weave.Actions/Context/` |
| Per-feature action class | takes typed `HttpClient` via DI; owns its wire DTO + `JsonSerializerContext` | `Weave.Actions/<Verb>/` |
| `AddSiloActions(...)` | composition extension; registers `HttpClient<TAction>` per verb | `Weave.Actions/SiloActionsServiceCollectionExtensions.cs` |
| Frontend bindings | `ConsoleActionPrompter`/`ConsoleActionReporter` (CLI) | `csharp/extensions/Weave.Cli.Shell/` |

The planned Phase 0 and Phase 1 verbs and the later ones (channels, skills, templates, marketplace, audit, workspace start/stop/watch) are actions under `csharp/extensions/Weave.Actions/`; the old `WorkspaceApiClient` path is gone.

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
