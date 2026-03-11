# Weave — Development Guide

## Build & Test

```bash
dotnet build Weave.slnx          # Build entire solution
dotnet test Weave.slnx           # Run all tests
dotnet test tests/Weave.Workspaces.Tests   # Run specific test project
dotnet test tests/Weave.Agents.Tests
dotnet test tests/Weave.Security.Tests
dotnet test tests/Weave.Tools.Tests
dotnet test tests/Weave.Deploy.Tests
```

Target framework: `net10.0`. The solution uses central package management (`Directory.Packages.props`) — add package versions there, not in individual `.csproj` files.

## Core Rules

### Zero Allocation

All hot paths must be allocation-free. This is a hard requirement, not a guideline.

- **Use `ReadOnlySpan<T>` / `Span<T>`** instead of allocating substrings — `string.AsSpan()`, `stackalloc`, slice operations
- **Use `ArrayPool<T>.Shared`** for temporary buffers — always return via `try/finally`
- **Use `StringValues`** for HTTP headers — avoids string[] allocation
- **Use `ValueTask<T>`** over `Task<T>` when the result is often synchronous (cached, already complete)
- **Use `[SkipLocalsInit]`** on performance-critical methods
- **Use `ref struct`** for transient processing types that must not escape the stack
- **Use `string.Create()`** with `SpanAction` instead of `StringBuilder` for one-shot string building
- **Use `Enumerable.TryGetNonEnumeratedCount()`** before materializing collections
- **Avoid LINQ in hot paths** — use `foreach` loops, manual aggregation, or `Span<T>` iteration
- **Avoid `async` state machines** when the method can return synchronously — check with `ValueTask.IsCompleted`
- **Avoid closures/lambdas that capture** in hot paths — use static lambdas or pass state via `object? state` parameters
- **Use `frozen collections`** (`FrozenDictionary`, `FrozenSet`) for lookup tables that don't change after initialization
- **Pool objects** with `ObjectPool<T>` for frequently allocated short-lived objects
- **Struct enumerators** — when implementing `IEnumerable<T>`, provide a `struct` `GetEnumerator()` to avoid boxing

### AOT Friendly

All code must be NativeAOT compatible. The CLI (`Weave.Cli`) is the AOT entry point.

- **No `System.Reflection.Emit`** — use source generators instead
- **No `Type.MakeGenericType()`** at runtime — all generic types must be statically known
- **Use `[JsonSerializable]`** source generators for all JSON serialization — never rely on reflection-based `System.Text.Json`
- **Use `[GeneratedRegex]`** for all regex patterns — never `new Regex()` at runtime
- **Use `[GenerateSerializer]`** (Orleans) for all grain state — this uses source generation, not reflection
- **Annotate with `[DynamicallyAccessedMembers]`** when reflection is unavoidable (e.g., DI registration)
- **Avoid `dynamic`** — use concrete types or interfaces
- **No `Assembly.Load*`** at runtime — all assemblies must be statically referenced
- **Use `LibraryImport`** over `DllImport` for P/Invoke (source-generated marshalling)
- **Trim-safe code** — suppress `IL2XXX` warnings only with `[UnconditionalSuppressMessage]` and a justification comment
- Projects that cannot be AOT (Orleans silo, Blazor Server) are marked `<IsAotExcluded>true</IsAotExcluded>`
- The `Weave.Cli` project must always remain AOT-publishable: `dotnet publish src/Weave.Cli -c Release -r linux-x64`

### Performance Validation

- Run `dotnet build -warnaserror` — zero warnings allowed
- Use `[Benchmark]` (BenchmarkDotNet) for any new hot path — measure allocations with `[MemoryDiagnoser]`
- Profile with `dotnet-counters` and `dotnet-trace` before and after changes to hot paths

## Project Structure

```
src/
  Weave.Shared/            Shared kernel — no project dependencies
  Weave.ServiceDefaults/   Aspire service defaults (OpenTelemetry, health checks)
  Weave.Workspaces/        Workspace domain — depends on Shared
  Weave.Agents/            Agent domain — depends on Shared + Workspaces
  Weave.Security/          Security slice — depends on Shared
  Weave.Tools/             Tool connectors + grains — depends on Shared + Workspaces + Security
  Weave.Deploy/            Deploy publishers — depends on Shared + Workspaces
  Weave.Dashboard/         Blazor Server + FluentUI — depends on all above
  Weave.Cli/               Spectre.Console CLI — depends on Shared + Workspaces + Deploy
  Weave.Silo/              Orleans silo host — depends on all domain slices
  Weave.AppHost/           Aspire orchestrator — references Silo + Dashboard
  Weave.SourceGen/         Branded ID source generator (netstandard2.0)
tests/
  Weave.Workspaces.Tests/
  Weave.Agents.Tests/
  Weave.Security.Tests/
  Weave.Tools.Tests/
  Weave.Deploy.Tests/
```

Dependency flow: `Shared → Workspaces → Agents/Security/Tools/Deploy → Silo/Dashboard/Cli → AppHost`. Never introduce circular references.

## Architecture Patterns

### Orleans Grains

Grains are the core unit of state and concurrency. Every grain:
- Implements an `IGrainWith*Key` interface in a separate file (`IFooGrain.cs`)
- Uses primary constructors for DI (`Grain(IDep dep) : Grain, IFooGrain`)
- Initializes state in `OnActivateAsync` using `this.GetPrimaryKeyString()`
- Also handles direct instantiation (for unit tests) by initializing state with sensible defaults in the field declaration

Grain key conventions:
- Workspace grains: `{workspaceId}` (string key)
- Agent grains: `{workspaceId}/{agentName}` (composite string key)
- Tool grains: `{workspaceId}/{toolName}` (composite string key)
- Heartbeat grains: `{workspaceId}/{agentName}` (mirrors agent key)
- Tool registry: `{workspaceId}` (one per workspace)
- Secret proxy: `{workspaceId}` (one per workspace)

### CQRS

Commands and queries live alongside their domain (`Workspaces/Commands/`, `Agents/Commands/`, `Agents/Queries/`).

Each file contains both the message record and its handler:

```csharp
public sealed record FooCommand(string Id, string Data);

public sealed class FooHandler(IGrainFactory grainFactory)
    : ICommandHandler<FooCommand, ResultType>
{
    public async Task<ResultType> HandleAsync(FooCommand command, CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<IFooGrain>(command.Id);
        return await grain.DoSomethingAsync(command.Data);
    }
}
```

Handlers are registered via `AddCqrs(assemblies...)` which scans for `ICommandHandler<,>` and `IQueryHandler<,>`.

### Domain Events

Events extend `DomainEvent` (which provides `EventId`, `Timestamp`, `SourceId`). Use `[Id(N)]` attributes starting at `3` for event-specific fields (0–2 are reserved by the base record).

```csharp
[GenerateSerializer]
public sealed record FooHappenedEvent : DomainEvent
{
    [Id(3)] public required string SomeField { get; init; }
}
```

Publish via `IEventBus.PublishAsync()`. Subscribe via `IEventBus.Subscribe<T>()`.

### Lifecycle Hooks

Use `ILifecycleManager.RunHooksAsync(phase, context, ct)` at state transitions. Always run hooks in pairs (e.g., `WorkspaceStarting` before the action, `WorkspaceStarted` after). Use `context with { Phase = ... }` to create the post-action context.

### Models

All Orleans-serializable models use `[GenerateSerializer]` and `[Id(N)]` on every serialized property. Prefer `sealed record` for immutable data. Use `sealed record` with mutable setters only for state objects that grains mutate in place.

### Branded IDs

Use strongly-typed ID structs instead of raw `string` for entity identifiers. IDs are defined in `Weave.Shared/Ids/BrandedIds.cs` using the `[BrandedId]` source generator attribute:

```csharp
[BrandedId]
public readonly partial record struct WorkspaceId;
```

The source generator (`Weave.SourceGen`) produces a full struct with: `Value` property, `New()` (GUID v7), `From(string)`, `Parse`/`TryParse`, `Empty`/`IsEmpty`, implicit `operator string`, `ToString()`, `CompareTo`, comparison operators, `TypeConverter`, and Orleans `[GenerateSerializer]` + `[Id(0)]`.

**Usage patterns:**
- Create new IDs: `WorkspaceId.New()` (uses `Guid.CreateVersion7()` for time-sortable UUIDs)
- Wrap existing values: `WorkspaceId.From(someString)`
- Grain keys remain `string` — convert at grain boundaries: `WorkspaceId.From(this.GetPrimaryKeyString())`
- Pass to grain factory using `.ToString()`: `grainFactory.GetGrain<IFooGrain>(workspaceId.ToString())`
- Assign to `DomainEvent.SourceId` (which is `string`) — implicit conversion handles this
- Nullable value types work naturally: `NetworkId?` for optional IDs

**Available branded IDs:** `WorkspaceId`, `AgentId`, `AgentTaskId`, `ContainerId`, `NetworkId`

To add a new branded ID, add a `[BrandedId] public readonly partial record struct FooId;` to `BrandedIds.cs`.

## Feature Slices

### Security (`Weave.Security`)

- **Capability Tokens** (`Tokens/`): HMAC-SHA256 signed tokens with grants (e.g., `tool:*`, `secret:*`), expiry, and revocation tracking
- **Leak Scanner** (`Scanning/`): 15+ `[GeneratedRegex]` patterns + Shannon entropy analysis, scans both request and response payloads
- **Transparent Secret Proxy** (`Proxy/`): Kestrel middleware — replaces `{secret:X}` placeholders at network boundary, scans responses for leaks
- **Vault Integration** (`Vault/`): `ISecretProvider` with `VaultSecretProvider` (HashiCorp Vault) and `InMemorySecretProvider` (dev/test)
- **Secret Proxy Grain** (`Grains/`): Per-workspace grain managing secret proxy routing

### Tools (`Weave.Tools`)

- **Connectors** (`Connectors/`): `IToolConnector` for MCP (stdio JSON-RPC), Dapr (service invocation), CLI (shell), OpenAPI (HTTP)
- **Discovery** (`Discovery/`): `IToolDiscoveryService` resolves the right connector for each `ToolType`
- **Tool Grain** (`Grains/`): Validates capability tokens, runs leak scanner on input/output, delegates to connectors
- **Events** (`Events/`): `ToolConnected`, `ToolDisconnected`, `ToolInvocationCompleted`, `ToolInvocationBlocked`

### Agents (`Weave.Agents`)

- **Agent Grain** (`Grains/`): LLM loop via `IChatClient`, tool dispatch, state management
- **Heartbeat** (`Heartbeat/`): Cron-triggered proactive agent wake-up via Orleans timers
- **Pipeline** (`Pipeline/`): `IChatClient` middleware — `CostTrackingChatClient` (token usage) and `RateLimitingChatClient` (token bucket)

### Deploy (`Weave.Deploy`)

- **Publishers** (`Translators/`): `IPublisher` implementations — Docker Compose, Kubernetes, Nomad HCL, Fly.io TOML, GitHub Actions workflows

### Dashboard (`Weave.Dashboard`)

- Blazor Server + FluentUI. Pages: Home (stats), Workspaces (CRUD), Agents (chat console), Setup (wizard)

### CLI (`Weave.Cli`)

- Spectre.Console `CommandApp` routing. Commands: `workspace new/list/remove`, `up`, `down`, `status`, `publish`, `config show/validate`

## C# Conventions

- **File-scoped namespaces** — always (`namespace Foo;` not `namespace Foo { }`)
- **Primary constructors** — prefer for DI injection
- **Top-level statements** — for `Program.cs` entry points
- **`sealed`** — all concrete classes unless designed for inheritance
- **`required`** — on init-only properties that must be set at construction
- **Records** — for data transfer objects and domain events
- **Collection literals** — use `[]` not `new List<T>()`
- **Private fields** — `_camelCase` prefix
- **Pattern matching** — prefer `is`, `is not`, switch expressions
- **`CultureInfo.InvariantCulture`** — use with `StringBuilder.AppendLine` and `string.Create` for interpolated strings (CA1305)
- **`StringComparison.Ordinal`** — use with `StartsWith`, `Contains`, etc. (CA1310)
- **`[GeneratedRegex]`** — all regex patterns must be source-generated (AOT + zero-allocation)
- **`ValueTask<T>`** — use over `Task<T>` for frequently synchronous operations
- **`stackalloc` / `ArrayPool`** — for temporary buffers in hot paths

## Testing Patterns

Tests use xunit v3 (`xunit.v3`) + Shouldly + NSubstitute. `Xunit`, `NSubstitute`, and `Shouldly` are globally imported via `tests/Directory.Build.props`.

**Do not use FluentAssertions** — it has a commercial license. Use **Shouldly** for all test assertions.

Grain tests instantiate grains directly (not through Orleans runtime). This means `OnActivateAsync` is NOT called — grains must handle this by initializing state defensively.

```csharp
private static (MyGrain Grain, IDep1 Dep1, IDep2 Dep2) CreateGrain()
{
    var dep1 = Substitute.For<IDep1>();
    var dep2 = Substitute.For<IDep2>();
    var logger = Substitute.For<ILogger<MyGrain>>();
    var grain = new MyGrain(dep1, dep2, logger);
    return (grain, dep1, dep2);
}
```

Test naming: `MethodName_Condition_ExpectedResult` (e.g., `ActivateAgentAsync_WhenAlreadyActive_ReturnsCurrentState`).

## Security

- Secrets use `SecretValue` (AES-256-GCM encrypted in memory) — never store plain text
- `SecretSafeException` auto-redacts secrets from error messages
- Security analyzer diagnostics are treated as errors
- Container specs default to `ReadOnly = true` and `DropAllCapabilities = true`
- Never commit `.env` files, credentials, or vault tokens
- All tool invocations are scanned for secret leaks before and after execution

## Adding a New Grain

1. Create `IFooGrain.cs` interface in `Grains/`
2. Create `FooGrain.cs` implementation in `Grains/`
3. Add state model in `Models/FooState.cs` with `[GenerateSerializer]`
4. Add domain events in `Events/FooEvents.cs`
5. Add CQRS commands/queries in `Commands/` and `Queries/`
6. Add unit tests — mock deps with NSubstitute, assert with FluentAssertions
7. Register any new services in `Weave.Silo/Program.cs`

## Adding a New Tool Connector

1. Implement `IToolConnector` in `Weave.Tools/Connectors/`
2. Add the `ToolType` enum value in `Weave.Tools/Models/ToolDefinition.cs`
3. Register in `IToolDiscoveryService` and `Weave.Silo/Program.cs`
4. Add config model in `Weave.Workspaces/Models/WorkspaceManifest.cs` if needed
5. Add tests in `tests/Weave.Tools.Tests/`

## Adding a New Deploy Publisher

1. Implement `IPublisher` in `Weave.Deploy/Translators/`
2. Add the target name to `PublishCommand` in `Weave.Cli/Commands/PublishCommand.cs`
3. Add tests in `tests/Weave.Deploy.Tests/`
