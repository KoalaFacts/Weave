# Weave — Development Guide

## Build & Test

```bash
dotnet build Weave.slnx          # Build entire solution
dotnet test Weave.slnx           # Run all tests
dotnet test tests/Weave.Workspaces.Tests   # Run specific test project
dotnet test tests/Weave.Agents.Tests
```

Target framework: `net10.0`. The solution uses central package management (`Directory.Packages.props`) — add package versions there, not in individual `.csproj` files.

## Project Structure

```
src/
  Weave.Shared/         # Shared kernel — no project dependencies
  Weave.ServiceDefaults/ # Aspire service defaults (OpenTelemetry, health checks)
  Weave.Workspaces/      # Workspace domain — depends on Shared
  Weave.Agents/          # Agent domain — depends on Shared + Workspaces
  Weave.Silo/            # Orleans silo host — depends on all above
  Weave.AppHost/         # Aspire orchestrator — references Silo
tests/
  Weave.Workspaces.Tests/
  Weave.Agents.Tests/
```

Dependency flow: `Shared → Workspaces → Agents → Silo → AppHost`. Never introduce circular references.

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
- Tool registry: `{workspaceId}` (one per workspace)

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

## Testing Patterns

Tests use xunit + FluentAssertions + NSubstitute. Requires `using Xunit;` (not auto-imported).

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

## Adding a New Grain

1. Create `IFooGrain.cs` interface in `Grains/`
2. Create `FooGrain.cs` implementation in `Grains/`
3. Add state model in `Models/FooState.cs` with `[GenerateSerializer]`
4. Add domain events in `Events/FooEvents.cs`
5. Add CQRS commands/queries in `Commands/` and `Queries/`
6. Add unit tests — mock deps with NSubstitute, assert with FluentAssertions
7. Register any new services in `Weave.Silo/Program.cs`
