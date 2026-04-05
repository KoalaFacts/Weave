# Runtime

The Runtime subsystem is the hosting and orchestration layer: the Orleans grain host (Silo), .NET Aspire app host, and shared service defaults.

## Projects

| Project | Purpose |
|---------|---------|
| `Weave.Silo` | Orleans grain host, REST API, plugin connectors, event bus implementations |
| `Weave.AppHost` | .NET Aspire orchestrator for local development |
| `Weave.ServiceDefaults` | Shared observability, resilience, and health check configuration |

## Weave.Silo

### Startup (Program.cs)

The silo bootstraps in this order:

1. **Orleans configuration**
   - **Local mode** (`Weave:LocalMode` or missing `Orleans:ClusterId`): localhost clustering + in-memory grain storage
   - **Distributed mode**: Aspire Orleans extensions with Redis

2. **Core services**
   - `ILifecycleManager` (singleton)
   - `IWorkspaceRuntime` — `InProcessRuntime` (local) or `PodmanRuntime` (distributed)

3. **CQRS** — `AddGeneratedCqrsHandlers()` (source-generated, zero reflection)

4. **Security**
   - `ICapabilityTokenService` — token-based capability model
   - `ILeakScanner` — secret leak detection
   - `TransparentSecretProxy` — secret masking

5. **Plugin system (hot-swappable)**
   - `PluginServiceBroker` — mutable service slots
   - `EventBusProxy` / `SecretProviderProxy` — delegate to broker's current instance
   - Default fallbacks: `InProcessEventBus`, `InMemorySecretProvider`

6. **Tool connectors**: `McpToolConnector`, `CliToolConnector`, `OpenApiToolConnector`, `DirectHttpToolConnector`

7. **Plugin connectors**: `DaprPluginConnector`, `VaultPluginConnector`, `HttpPluginConnector`, `WebhookPluginConnector`

8. **Auto-detection** — discovers Dapr (`DAPR_HTTP_PORT`) and Vault (`Vault:Address`) at startup

9. **API endpoints** — maps REST routes

### API Endpoints

#### Workspaces (`/api/workspaces`)

| Method | Path | Purpose |
|--------|------|---------|
| `GET` | `/` | List all workspaces |
| `POST` | `/` | Start workspace (with manifest) |
| `GET` | `/{id}` | Get workspace state |
| `DELETE` | `/{id}` | Stop workspace |

#### Agents (`/api/workspaces/{id}/agents`)

| Method | Path | Purpose |
|--------|------|---------|
| `GET` | `/` | List agents in workspace |
| `GET` | `/{name}` | Get agent state |
| `POST` | `/{name}/activate` | Activate agent |
| `POST` | `/{name}/deactivate` | Deactivate agent |
| `POST` | `/{name}/messages` | Send message to agent |
| `POST` | `/{name}/tasks` | Submit task |
| `POST` | `/{name}/tasks/{id}/complete` | Complete task with proof |
| `POST` | `/{name}/tasks/{id}/review` | Review task |

#### Tools (`/api/workspaces/{id}/tools`)

| Method | Path | Purpose |
|--------|------|---------|
| `GET` | `/` | List connected tools |
| `GET` | `/{name}` | Get tool connection |

#### Plugins (`/api/plugins`)

| Method | Path | Purpose |
|--------|------|---------|
| `GET` | `/` | Get all plugin statuses |
| `GET` | `/catalog` | Get plugin schema catalog |
| `POST` | `/` | Connect plugin |
| `DELETE` | `/{name}` | Disconnect plugin |

### Plugin Connectors

#### DaprPluginConnector

- **Provides**: events, tools
- **Connect**: creates `DaprEventBus` + `DaprToolConnector`, swaps via broker
- **Detection**: `DAPR_HTTP_PORT` environment variable
- **Communication**: HTTP to Dapr sidecar (`http://localhost:{port}`)

#### VaultPluginConnector

- **Provides**: secrets
- **Connect**: creates `VaultSecretProvider`, swaps via broker
- **Detection**: `Vault:Address` config
- **Auth**: `X-Vault-Token` header (optional, from config or `VAULT_TOKEN` env)

#### HttpPluginConnector

- **Provides**: http
- **Connect**: creates named HTTP client, stores in broker by key `http:{name}`

#### WebhookPluginConnector

- **Provides**: events
- **Connect**: creates `WebhookEventBus`, swaps via broker
- **Behavior**: POSTs events to webhook URL with `X-Weave-Topic` header

### Event Bus Implementations

| Implementation | Transport | Behavior |
|----------------|-----------|----------|
| `InProcessEventBus` | In-memory | Default fallback, local handlers only |
| `DaprEventBus` | Dapr HTTP | Publishes to `pubsub` topic + local handlers |
| `WebhookEventBus` | HTTP POST | Posts to webhook URL + local handlers |

### JSON Serialization

All API responses use source-generated `SiloApiJsonContext`:
- `camelCase` naming
- Null values skipped
- AOT-compatible (no reflection)

## Weave.AppHost

Aspire orchestration topology for local development:

```
Redis (persistent)
    ↓
Orleans Cluster ("weave-cluster")
    ├── Clustering: Redis
    └── Grain Storage: Redis
    ↓
Silo ("weave-silo") × 2 replicas
    ├── References: Orleans, Redis
    └── Dapr sidecar (AppId: "weave-silo")
        ├── State Store ("statestore")
        └── Pub/Sub ("pubsub")
    ↓
Dashboard ("dashboard")
    └── References: Silo (waits for startup)
```

## Weave.ServiceDefaults

Shared extension method `AddServiceDefaults()` providing:

- **OpenTelemetry**: logging, metrics (AspNetCore, HttpClient, Runtime), tracing (AspNetCore, HttpClient), conditional OTLP export
- **Service discovery**: Aspire-compatible
- **HTTP resilience**: standard resilience handler on all `HttpClient` instances
- **Health checks**: `/health` and `/alive` endpoints

## Request Flow Example: Starting a Workspace

```
POST /api/workspaces { manifest }
  → WorkspaceEndpoints
    → StartWorkspaceCommand
      → ICommandDispatcher (source-generated)
        → StartWorkspaceHandler
          → IWorkspaceGrain.StartAsync()
          → IWorkspaceRegistryGrain.RegisterAsync()
          → IToolRegistryGrain.ConnectToolsAsync()
          → IAgentSupervisorGrain.ActivateAllAsync()
          → IHeartbeatGrain.StartAsync()
  ← WorkspaceResponse (201 Created)
```

## Plugin Hot-Swap Flow

```
POST /api/plugins { name: "dapr", type: "dapr", config: { port: "3500" } }
  → PluginRegistry.ConnectAsync()
    → DaprPluginConnector.ConnectAsync()
      → Create DaprEventBus
      → broker.Swap<IEventBus>(daprEventBus)  // hot-swap
      → toolDiscovery.Register(daprToolConnector)
  ← PluginStatus { IsConnected: true }
```
