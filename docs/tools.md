# Tools

> **Source**: `src/Tools/` | **Depends on**: [Foundation](foundation.md), [Workspaces](workspaces.md), [Security](security.md) | **Depended on by**: [Assistants](assistants.md), [Runtime](runtime.md)
> **See also**: [index](index.md)

The Tools subsystem provides tool connectors, discovery, and grain-based tool execution with security scanning and lifecycle management.

## Projects

| Project | Purpose |
|---------|---------|
| `Weave.Tools` | Connectors, discovery service, tool grain, models, events |
| `Weave.Tools.Tests` | Tests for connectors, grain, discovery, security scanning |

## IToolConnector Interface

```csharp
public interface IToolConnector
{
    ToolType ToolType { get; }
    Task<ToolHandle> ConnectAsync(string connectionId, ToolSpec spec, CapabilityToken token, CancellationToken ct);
    Task DisconnectAsync(string connectionId, CancellationToken ct);
    Task<ToolResult> InvokeAsync(string connectionId, ToolInvocation invocation, CancellationToken ct);
    Task<ToolSchema> DiscoverSchemaAsync(string connectionId, CancellationToken ct);
}
```

## Connectors

### McpToolConnector (`mcp`)

Spawns external MCP servers as subprocesses. Communicates via JSON-RPC 2.0 over stdin/stdout. Kills the entire process tree on disconnect.

### CliToolConnector (`cli`)

Executes shell commands with configurable allow/deny lists.

- Denied commands take precedence over allowed.
- Wildcard pattern matching (e.g., `git*` matches start).
- Appends shell-specific arguments (`-c` for bash, `-Command` for PowerShell).
- Captures stdout/stderr and exit codes.

### OpenApiToolConnector (`openapi`)

HTTP API integration using `HttpClient`.

- Parameters: `endpoint` (path), `http_method` (GET or POST).
- Supports bearer token authentication.
- POST sends `RawInput` as JSON body.

### DaprToolConnector (`dapr`)

Dapr service invocation via the sidecar HTTP API.

- Invokes `/v1.0/invoke/{appId}/method/{method}`.
- Assumes sidecar at `http://localhost:3500`.
- No Dapr SDK dependency — pure HTTP.

### DirectHttpToolConnector (`direct_http`)

Lightweight direct HTTP calls with security protections.

- Validates method path — rejects `..`, `://`, `\\`, `%`, `@` (SSRF protection).
- Per-tool auth headers stored in `ConcurrentDictionary`.
- Constructs URL: `{baseUrl}/{method}`.

### FileSystemToolConnector (`filesystem`)

Sandboxed file system access for agents.

- **Path sandboxing**: all paths resolved relative to a configured root directory. Rejects `..`, absolute paths, drive letters, URL schemes, and null bytes.
- **Operations**: `read_file`, `write_file`, `list_directory`, `search_files`, `file_info`.
- **Read-only mode**: optional config to disable all write operations.
- **Binary detection**: scans first 8KB for null bytes — rejects binary files from text reads.
- **Size limits**: configurable `MaxReadBytes` (default 1MB) prevents unbounded reads.
- **Glob search**: `search_files` uses `Directory.EnumerateFiles` with capped results (1000).

## Tool Grain

**Key**: `{workspaceId}/{toolName}` — Orleans grain managing per-tool lifecycle.

### Interface

```csharp
public interface IToolGrain : IGrainWithStringKey
{
    Task ConnectAsync(ToolSpec spec, CapabilityToken token);
    Task DisconnectAsync();
    Task<ToolResult> InvokeAsync(ToolInvocation invocation, CapabilityToken token);
    Task<ToolSchema> GetSchemaAsync();
    Task<ToolHandle> GetHandleAsync();
}
```

### Security Features

- **Token validation**: requires grant `tool:{name}` or `tool:*`.
- **Outbound scanning**: scans payload before sending — blocks invocation on leak detection.
- **Inbound scanning**: scans response on success — redacts if leak detected.
- **Secret substitution**: delegates to `ISecretProxyGrain` for `{secret:path}` placeholder replacement.

### Lifecycle Hooks

Calls `ILifecycleManager` for phases: `ToolConnecting`, `ToolConnected`, `ToolDisconnecting`, `ToolDisconnected`.

## Tool Discovery Service

```csharp
public interface IToolDiscoveryService
{
    IToolConnector GetConnector(ToolType type);
    IReadOnlyList<ToolType> SupportedTypes { get; }
    void Register(IToolConnector connector);
    void Unregister(ToolType type);
}
```

- Separates built-in connectors (frozen at startup) from dynamic (plugin-registered).
- Dynamic connectors override built-in types.
- Thread-safe with `Lock` and cache invalidation on register/unregister.

## Models

### ToolType

```csharp
enum ToolType { Mcp, Dapr, OpenApi, Cli, Library, DirectHttp, FileSystem }
```

### ToolSpec

```csharp
record ToolSpec {
    string Name;
    ToolType Type;
    McpConfig? Mcp;
    DaprToolConfig? Dapr;        // { AppId, MethodName }
    OpenApiConfig? OpenApi;
    CliConfig? Cli;
    DirectHttpToolConfig? DirectHttp;  // { BaseUrl, AuthHeader? }
    FileSystemToolConfig? FileSystem;  // { Root, ReadOnly, MaxReadBytes }
}
```

### ToolInvocation & ToolResult

```csharp
record ToolInvocation {
    string ToolName;
    string Method;
    Dictionary<string, string> Parameters;
    string? RawInput;
}

record ToolResult {
    bool Success;
    string Output;
    string? Error;
    TimeSpan Duration;
    string ToolName;
}
```

### ToolSchema

```csharp
record ToolSchema {
    string ToolName;
    string Description;
    List<ToolParameter> Parameters;  // { Name, Type, Description, Required }
}
```

### ToolHandle

```csharp
record ToolHandle {
    string ToolName;
    ToolType Type;
    string ConnectionId;
    bool IsConnected;
}
```

## Events

| Event | Fields |
|-------|--------|
| `ToolInvocationCompletedEvent` | `ToolName`, `WorkspaceId`, `Success`, `Duration` |
| `ToolInvocationBlockedEvent` | `ToolName`, `WorkspaceId`, `Reason` |

## Testing

Key test patterns:

- **Connection tests**: validate required configs, successful connect/disconnect
- **Command filtering** (CLI): allow/deny lists, wildcard patterns, precedence
- **Auth tests** (OpenApi/DirectHttp): header set/cleared correctly
- **HTTP tests**: `StubHandler : HttpMessageHandler` for test isolation
- **Token validation** (ToolGrain): invalid/expired tokens throw `UnauthorizedAccessException`
- **Secret scanning** (ToolGrain): AWS keys blocked outbound, redacted inbound
- **SSRF protection** (DirectHttp): rejects path traversal and injection patterns
- **Discovery service**: register/unregister dynamic connectors, cache invalidation
