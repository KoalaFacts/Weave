# Workspaces

> **Source**: `src/Workspaces/` | **Depends on**: [Foundation](foundation.md) | **Depended on by**: [Assistants](assistants.md), [Tools](tools.md), [Security](security.md), [Deployment](deployment.md), [Runtime](runtime.md), [UX](ux.md)
> **See also**: [index](index.md)

The Workspaces subsystem handles workspace manifest parsing, validation, runtime environment provisioning, and workspace lifecycle management through Orleans grains.

## Projects

| Project | Purpose |
|---------|---------|
| `Weave.Workspaces` | Manifest model, parser, runtime abstraction, grains, plugins |
| `Weave.Workspaces.Tests` | Unit tests for parsing, grains, runtime, plugins, commands |

## Workspace Manifest

The manifest (`workspace.json`) is the single source of truth for a workspace. It uses JSONC (JSON with comments and trailing commas).

### Root Model

```csharp
WorkspaceManifest {
    Version: string             // Required, must be "1.0"
    Name: string                // Required
    Workspace: WorkspaceConfig
    Agents: Dictionary<string, AgentDefinition>
    Tools: Dictionary<string, ToolDefinition>
    Targets: Dictionary<string, TargetDefinition>
    Hooks: HooksConfig?
    Plugins: Dictionary<string, PluginDefinition>
}
```

### Workspace Configuration

```csharp
WorkspaceConfig {
    Isolation: IsolationLevel   // Full, Shared, None (default: Full)
    Network: NetworkConfig?     // { Name, Subnet }
    Filesystem: FilesystemConfig?  // { Root, Mounts[] }
    Secrets: SecretsConfig?     // { Provider ("env"), Vault? }
}
```

### Agent Definition

```csharp
AgentDefinition {
    Model: string               // Required (e.g. "claude-sonnet-4-20250514")
    SystemPromptFile: string?
    MaxConcurrentTasks: int     // Default: 1
    Memory: MemoryConfig?       // { Provider, Ttl }
    Tools: List<string>         // References to Tools dictionary
    Capabilities: List<string>
    Heartbeat: HeartbeatConfig? // { Cron, Tasks[] }
    Target: TargetSelector?     // { Labels[] }
}
```

### Tool Definition

```csharp
ToolDefinition {
    Type: string                // Required: mcp, dapr, openapi, cli, library, direct_http
    Mcp: McpConfig?             // { Server, Args[], Env{} }
    OpenApi: OpenApiConfig?     // { SpecUrl, Auth? }
    Cli: CliConfig?             // { Shell, AllowedCommands[], DeniedCommands[] }
    DirectHttp: DirectHttpConfig? // { BaseUrl, Auth? }
}
```

### Target Definition

```csharp
TargetDefinition {
    Runtime: string             // Required
    Replicas: int               // Default: 1
    Trigger: string?            // For CI (e.g. "pull_request")
    Region: string?             // For cloud (e.g. "iad")
    Scaling: ScalingConfig?     // { Min, Max }
}
```

### Plugin Definition

```csharp
PluginDefinition {
    Type: string                // Required: dapr, vault, http, webhook, custom
    Description: string?
    Config: Dictionary<string, string>
    EnabledWhen: string?
}
```

### Hooks Configuration

```csharp
HooksConfig {
    Workspace: WorkspaceHooks?  // PreStart, PostStart, PreStop, PostStop
    Agents: Dict<string, AgentHooks>?   // OnActivated, OnDeactivated, OnError
    Tools: Dict<string, ToolHooks>?     // OnConnected, OnDisconnected, OnError
}
```

## Manifest Parser

### Interface

```csharp
public interface IManifestParser
{
    WorkspaceManifest Parse(string json);
    WorkspaceManifest ParseFile(string path);
    string Serialize(WorkspaceManifest manifest);
    IReadOnlyList<string> Validate(WorkspaceManifest manifest);
}
```

### JSONC Support

The `ManifestJsonContext` is configured with:
- `JsonCommentHandling.Skip` — single-line `//` and block `/* */` comments
- `AllowTrailingCommas = true`
- `PropertyNamingPolicy = SnakeCaseLower` — C# PascalCase maps to JSON snake_case
- `UseStringEnumConverter = true` — enums as strings

### Validation Rules

- `version` and `name` are required
- Version must be exactly `"1.0"`
- Agents must have a non-empty `model`
- Agent tool references must exist in the `Tools` dictionary
- Agent heartbeats must include a `cron` value
- Tool types must be one of: `mcp`, `dapr`, `openapi`, `cli`, `library`, `direct_http`
- Plugin types must be one of: `dapr`, `vault`, `http`, `webhook`, `custom`
- Targets must have a non-empty `runtime`

## Runtime Abstraction

### Interface

```csharp
public interface IWorkspaceRuntime
{
    string RuntimeName { get; }
    Task<WorkspaceEnvironment> ProvisionAsync(WorkspaceManifest manifest, CancellationToken ct);
    Task TeardownAsync(WorkspaceId workspaceId, CancellationToken ct);
    Task<ContainerHandle> StartContainerAsync(ContainerSpec spec, CancellationToken ct);
    Task StopContainerAsync(ContainerId containerId, CancellationToken ct);
    Task<NetworkHandle> CreateNetworkAsync(NetworkSpec spec, CancellationToken ct);
    Task DeleteNetworkAsync(NetworkId networkId, CancellationToken ct);
}
```

### Implementations

| Runtime | Name | Description |
|---------|------|-------------|
| `InProcessRuntime` | `in-process` | No containers. Returns empty environment. For development/testing. |
| `PodmanRuntime` | `podman` | Uses Podman CLI for real container and network management with security defaults (read-only, drop all capabilities). |

### Container Spec

```csharp
ContainerSpec {
    Name, Image: string
    Environment: Dict<string, string>
    PortMappings: Dict<int, int>       // host → container
    Mounts: List<MountConfig>
    NetworkId: NetworkId?
    Command: List<string>
    ReadOnly: bool = false
    DropAllCapabilities: bool = true
    NoNetwork: bool = false
}
```

## Grains

### WorkspaceGrain

**Key**: `{workspaceId}` — **State**: `[PersistentState("workspace", "Default")]`

| Method | Behavior |
|--------|----------|
| `StartAsync(manifest)` | Guards double-start. Runs `WorkspaceStarting` hooks → provisions runtime → updates state → runs `WorkspaceStarted` hooks → publishes `WorkspaceStartedEvent`. |
| `StopAsync()` | Guards not-running. Runs `WorkspaceStopping` hooks → tears down runtime → clears state → runs `WorkspaceStopped` hooks → publishes `WorkspaceStoppedEvent`. |
| `GetStateAsync()` | Returns persisted state. |

**State transitions**: `Stopped → Starting → Running → Stopping → Stopped` (or `Error` on failure).

### WorkspaceRegistryGrain

**Key**: `"active"` (singleton) — **State**: `[PersistentState("workspace-registry", "Default")]`

Tracks all active workspace IDs. `RegisterAsync` is idempotent.

## Workspace State

```csharp
WorkspaceState {
    WorkspaceId: WorkspaceId
    Status: WorkspaceStatus     // Stopped, Starting, Running, Stopping, Error
    StartedAt, StoppedAt: DateTimeOffset?
    ActiveAgents, ActiveTools: List<string>
    Containers: List<ContainerInfo>
    NetworkId: NetworkId?
    ErrorMessage: string?
    Name: string?
}
```

## Commands & Queries

| Type | Name | Handler Behavior |
|------|------|-----------------|
| Command | `StartWorkspaceCommand` | Starts grain → registers in registry → connects tools → configures access → activates agents → starts heartbeats |
| Command | `StopWorkspaceCommand` | Stops heartbeats → deactivates agents → disconnects tools → stops grain → unregisters |
| Query | `GetWorkspaceStateQuery` | Delegates to `WorkspaceGrain.GetStateAsync()` |
| Query | `GetAllWorkspaceStatesQuery` | Queries registry → fetches all states → returns sorted |

## Events

| Event | Fields |
|-------|--------|
| `WorkspaceStartedEvent` | `WorkspaceName`, `AgentNames[]` |
| `WorkspaceStoppedEvent` | (inherits `DomainEvent`) |
| `WorkspaceErrorEvent` | `ErrorMessage` |

## Plugin Registry

Manages plugin lifecycle with hot-swap support.

### Features

- **Per-name locking** via `SemaphoreSlim` prevents concurrent connect/disconnect races.
- **Config resolution**: priority is explicit config > environment variable > schema default.
- **Validation**: checks required fields, returns errors with env var hints.
- **Make-before-break hot-swap**: new plugin connects first; old disconnects only on success.
- **Secret redaction**: fields marked `Secret = true` are replaced with `"***"` in status info.

### Interface

```csharp
public interface IPluginConnector
{
    string PluginType { get; }
    PluginSchema Schema { get; }
    Task<PluginStatus> ConnectAsync(string name, PluginDefinition definition);
    Task<PluginStatus> DisconnectAsync(string name);
    PluginStatus GetStatus(string name);
}
```

## Testing

Tests use xUnit v3, Shouldly, and NSubstitute. Key test files:

- `ManifestParserTests` — 50+ tests: parsing, validation, JSONC, round-trip serialization
- `WorkspaceGrainTests` — lifecycle transitions, error handling, hook invocation
- `InProcessRuntimeTests` — runtime name, no-op operations, grain integration
- `WorkspaceRegistryGrainTests` — idempotent register/unregister
- `PluginRegistryTests` — 30+ tests: connect/disconnect, hot-swap, config resolution, secret redaction
- `WorkspaceCommandHandlerTests` — full orchestration flows for start/stop
