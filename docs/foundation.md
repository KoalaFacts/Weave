# Foundation — Weave.Shared & Weave.SourceGen

> **Source**: `src/Foundation/` | **Depends on**: (none — root dependency) | **Depended on by**: all other subsystems
> **See also**: [index](index.md) · [workspaces](workspaces.md) · [assistants](assistants.md) · [tools](tools.md) · [security](security.md) · [deployment](deployment.md) · [runtime](runtime.md) · [ux](ux.md)

The Foundation layer provides the core abstractions, utilities, and source generators that all other Weave subsystems depend on.

## Projects

| Project | Target | Purpose |
|---------|--------|---------|
| `Weave.Shared` | `net10.0` | Branded IDs, CQRS, events, lifecycle, plugins, secrets |
| `Weave.SourceGen` | `netstandard2.0` | Roslyn incremental generators for branded IDs and CQRS registration |

## Branded IDs

Strongly typed identifiers using the "branded types" pattern. Each ID is a `readonly partial record struct` wrapping a `string` value.

### Declared IDs

| ID | Purpose |
|----|---------|
| `WorkspaceId` | Workspace identifier |
| `AgentId` | Agent identifier |
| `AgentTaskId` | Task identifier |
| `ContainerId` | Container identifier |
| `NetworkId` | Network identifier |

### Declaration

```csharp
[BrandedId]
public readonly partial record struct WorkspaceId;
```

### Generated Features

The `BrandedIdGenerator` (incremental source generator) produces a complete implementation for each annotated type:

- **Value property** and constructor
- **Factory methods**: `New()` (UUID v7), `From(string)`
- **Parsing**: `Parse()`, `TryParse()`
- **Empty sentinel**: `Empty` property, `IsEmpty` check
- **Conversions**: implicit to `string`, explicit from `string`
- **Comparison**: `IComparable<T>`, operators (`<`, `>`, `<=`, `>=`)
- **TypeConverter** for binding and serialization
- **Orleans serialization** (surrogate + converter, conditionally emitted when Orleans is referenced)

### Usage

```csharp
var id = WorkspaceId.New();          // Generate new UUID v7
var id2 = WorkspaceId.From("abc");   // From string
string s = id;                        // Implicit to string
bool empty = id.IsEmpty;             // Check empty
```

## CQRS

Command Query Responsibility Segregation with dispatcher + handler pattern.

### Interfaces

```csharp
public interface ICommandHandler<in TCommand, TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken ct);
}

public interface IQueryHandler<in TQuery, TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken ct);
}

public interface ICommandDispatcher
{
    Task<TResult> DispatchAsync<TCommand, TResult>(TCommand command, CancellationToken ct);
}

public interface IQueryDispatcher
{
    Task<TResult> DispatchAsync<TQuery, TResult>(TQuery query, CancellationToken ct);
}
```

### Dispatchers

`CommandDispatcher` and `QueryDispatcher` resolve handlers from `IServiceProvider` at dispatch time. Handlers are registered as scoped services.

### Registration

Two approaches:

1. **Reflection-based** (`AddCqrs(assemblies)`) — scans assemblies at startup. Marked `[RequiresUnreferencedCode]`.
2. **Source-generated** (`AddGeneratedCqrsHandlers()`) — the `CqrsRegistrationGenerator` scans the compilation and all referenced assemblies, emitting explicit `AddScoped` calls. Zero runtime reflection, AOT-safe.

## Events

Pub/sub domain event system.

### Core Types

```csharp
public interface IDomainEvent
{
    string EventId { get; }
    DateTimeOffset Timestamp { get; }
    string SourceId { get; }
}

public abstract record DomainEvent : IDomainEvent;

public interface IEventBus
{
    Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct) where TEvent : IDomainEvent;
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IDomainEvent;
}
```

### InProcessEventBus

In-memory implementation using `ConcurrentDictionary<Type, List<Delegate>>`. Thread-safe with snapshot-based dispatch — handlers are copied under lock before invocation so subscriptions can change mid-publish.

### EventBusProxy

A hot-swappable proxy registered as the `IEventBus` singleton. Uses read/write locking:

- **Publish** enters a read lock (concurrent publishes allowed).
- **Subscribe / Swap** enter a write lock (exclusive).
- On swap, all active subscriptions are replayed on the new bus.

## Lifecycle

Hook-based lifecycle management for workspace, agent, plugin, and tool components.

### Phases

```
Workspace:  Creating → Created → Starting → Started → Stopping → Stopped → Destroying → Destroyed
Agent:      Activating → Activated → Deactivating → Deactivated → Errored
Plugin:     Connecting → Connected → Disconnecting → Disconnected
Tool:       Connecting → Connected → Disconnecting → Disconnected → Invoking → Invoked → Errored
```

### Interfaces

```csharp
public interface ILifecycleHook
{
    LifecyclePhase Phase { get; }
    int Order { get; }
    Task ExecuteAsync(LifecycleContext context, CancellationToken ct);
}

public interface ILifecycleManager
{
    Task RunHooksAsync(LifecyclePhase phase, LifecycleContext context, CancellationToken ct);
    IDisposable Register(ILifecycleHook hook);
}
```

### LifecycleManager

- Maintains hooks grouped by phase, sorted by `Order`.
- Caches the grouping and rebuilds only when hooks are added/removed.
- Hooks execute sequentially within a phase.
- Registration returns an `IDisposable` for unsubscription.

### LifecycleContext

```csharp
[GenerateSerializer]
public sealed record LifecycleContext
{
    [Id(0)] public required WorkspaceId WorkspaceId { get; init; }
    [Id(1)] public string? AgentName { get; init; }
    [Id(2)] public string? ToolName { get; init; }
    [Id(3)] public LifecyclePhase Phase { get; init; }
    [Id(4)] public Dictionary<string, string> Properties { get; init; } = [];
}
```

## Plugin Infrastructure

### PluginServiceBroker

Runtime service slot swapping without rebuilding the DI container.

- **Typed slots**: `Get<T>()`, `Swap<T>(newService)`, `OnSwap<T>(callback)`
- **Named slots**: `Set(key, service)`, `Get<T>(key)`, `Remove(key)`
- Thread-safe via `Lock` for typed services, `ConcurrentDictionary` for named.

### Usage Pattern

```csharp
// Register callback for subscription replay
broker.OnSwap<IEventBus>(newBus => { /* re-subscribe */ });

// Hot-swap to Dapr event bus
var previous = broker.Swap<IEventBus>(daprEventBus);
```

## Secrets

### SecretValue

A `readonly struct` that encrypts plaintext with AES-GCM using a per-process random key.

- `ToString()` returns `"***REDACTED***"` (never leaks).
- `DecryptToString()` returns the actual value and zeros memory after use.
- `Equals()` uses constant-time comparison via `CryptographicOperations.FixedTimeEquals`.
- Orleans-serializable via `[GenerateSerializer]`.
- JSON serialization always writes `"***REDACTED***"`.

### SecretSafeException

An exception type that automatically redacts sensitive data (password, secret, token, key, credential, apikey, auth, bearer) from its message using pattern matching.

## Source Generators

### BrandedIdGenerator

- **Trigger**: `[BrandedId]` attribute on `partial readonly record struct`
- **Output**: One `.g.cs` per type with full implementation
- **Conditional**: Detects Orleans at compile time for serialization support

### CqrsRegistrationGenerator

- **Discovery**: Scans compilation + referenced assemblies for `ICommandHandler<,>` and `IQueryHandler<,>` implementations
- **Output**: `GeneratedCqrsRegistration.g.cs` with `AddGeneratedCqrsHandlers()` extension method
- **Advantages**: Zero reflection, AOT-safe, cross-assembly discovery

## Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.Orleans.Sdk` | Grain and serialization attributes |
| `Microsoft.Extensions.AI.Abstractions` | AI/ML abstractions |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | DI container |
| `Microsoft.Extensions.Logging.Abstractions` | Structured logging |
| `Microsoft.CodeAnalysis.CSharp` (SourceGen only) | Roslyn compiler API |
