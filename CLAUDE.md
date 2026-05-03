# Weave — Development Guide

> **Start here before writing code or tests:** [docs/best-practices.md](docs/best-practices.md) is the law of the repo.

## Build and Test

```bash
dotnet build Weave.slnx
dotnet test --solution Weave.slnx

dotnet test --project src/Workspaces/Weave.Workspaces.Tests
dotnet test --project src/Assistants/Weave.Agents.Tests
dotnet test --project src/Security/Weave.Security.Tests
dotnet test --project src/Tools/Weave.Tools.Tests
dotnet test --project src/Deployment/Weave.Deploy.Tests
dotnet test --project src/Runtime/Weave.Silo.Tests
dotnet test --project src/Foundation/Weave.Shared.Tests
dotnet test --project src/UX/Weave.Cli.Tests
```

Most projects target `net10.0`. `Weave.SourceGen` targets `netstandard2.0`.

The solution uses central package management through `Directory.Packages.props`. Add package versions there unless the project already opts out, such as `Weave.SourceGen`.

## Repository Rules

- Warnings are treated as errors.
- Prefer minimal, targeted changes.
- Match the existing code style in the touched project.
- Classes over 200 lines require a design check; prefer one production class per file.
- Keep the current dependency flow; do not introduce circular references.
- Do not use `FluentAssertions`. Tests use `Shouldly`.
- **Pre-1.0: no backward-compat shims.** No `Legacy*` constants, no dual config keys, no deprecated synonyms, no fallback reads. When a contract changes, change the call sites. See [docs/best-practices.md → Versioning and breaking changes](docs/best-practices.md).
- **One opt-in project per storage/transport provider.** Abstractions projects pull zero provider packages; impls live in `Weave.X.{Sqlite,Postgres,Redis,...}` siblings. Same doc.

## Project Layout

```text
src/
  Foundation/
    Weave.Shared/        Shared abstractions, branded IDs, CQRS, events, lifecycle
    Weave.SourceGen/     Source generators: branded IDs, CQRS registration (`netstandard2.0`)
  Workspaces/            Workspace manifest parsing (JSONC), runtime abstraction, plugin registry
  Assistants/            Agent actors, supervisor, heartbeat, chat pipeline, channels, skills, user model
  Tools/                 Tool connectors (MCP, CLI, OpenAPI, DirectHttp, FileSystem), discovery, marketplace
  Security/              Capability tokens, leak scanning, secret proxy, provider proxies
                         (Weave.Security holds abstractions + in-memory backends;
                          Weave.Security.Sqlite / Weave.Security.Postgres host the
                          storage-provider impls so the abstractions stay free of
                          Microsoft.Data.Sqlite / Npgsql)
  Deployment/            Deployment publishers
  Runtime/               Orleans host (Silo), Aspire app host, and shared service defaults
                         (Weave.Silo references Weave.Silo.Clustering.{Redis,Sqlite,
                          SqlServer,Postgres} so each Orleans backend is its own
                          opt-in dep — drop a project ref to ship a slim host)
  UX/                    Spectre.Console CLI and Blazor dashboard
```

Dependency flow should stay roughly:

`Shared -> Workspaces -> Agents/Tools/Security/Deploy -> Silo/Cli/Dashboard -> AppHost`

## Architecture Conventions

### Orleans

- Domain actor interfaces (`IAgentActor`, `IToolRegistryActor`, etc.) live in their domain projects.
- Orleans grain bridges (`IAgentActorGrain : IAgentActor, IGrainWithStringKey`) live in `Weave.Silo/VirtualActors/GrainInterfaces.cs`.
- Actor keys are string-based.
- Common key shapes:
  - workspace: `{workspaceId}`
  - agent: `{workspaceId}/{agentName}`
  - tool: `{workspaceId}/{toolName}`
  - heartbeat: `{workspaceId}/{agentName}`
- Grain state models use `[GenerateSerializer]` and `[Id(n)]`.
- Tests may instantiate actors directly, so implementations should not rely exclusively on `OnActivateAsync` for safe defaults.

### CQRS and API Flow

- Commands and queries live with their domain.
- `Weave.Silo` wires handlers through source-generated `AddGeneratedCqrsHandlers()` (from `CqrsRegistrationGenerator`).
- HTTP endpoints in `src/Runtime/Weave.Silo/Api` are thin adapters over CQRS dispatch.

### Branded IDs

- Strongly typed IDs are declared in `Weave.Shared/Ids/BrandedIds.cs`.
- The source generator in `Weave.SourceGen` expands `[BrandedId]` declarations.
- Use branded IDs inside domain code and convert to `string` only at grain or API boundaries.

## Implementation Notes

### AOT and trimming

The repo defaults to AOT-friendly settings in `Directory.Build.props`, but several projects are explicitly marked with `<IsAotExcluded>true</IsAotExcluded>`. Keep new code trimming-aware where practical, but do not assume every project is currently NativeAOT-ready.

### Serialization and source generation

- Prefer source-generated patterns already used in the repo.
- Use `[GeneratedRegex]` for regex definitions.
- Use Orleans serializers on models that cross grain boundaries.
- Workspace manifests use `ManifestJsonContext` (STJ source gen) with JSONC support (comments + trailing commas).

### Plugins (JSON-configured, environment-detected)

- Optional integrations (Dapr, Vault) are JSON-configured in the workspace manifest `plugins` section and environment-detected by the Silo at startup.
- No compiled plugin assemblies — the Silo is the control plane "brain" that wires HTTP-based adapters.
- Dapr: activated when `DAPR_HTTP_PORT` env var is set. Registers `DaprEventBus` and `DaprToolConnector` which call the Dapr sidecar HTTP API directly.
- Vault: activated when `Vault:Address` config is set. Registers `VaultSecretProvider` which calls the Vault HTTP API directly.
- All adapters use `HttpClient` with AOT-friendly serialization (`JsonSerializer.SerializeToUtf8Bytes` + `ByteArrayContent`, `JsonDocument` for response parsing).

### Performance

- Prefer straightforward code first.
- Follow existing low-allocation patterns in hot paths.
- Avoid speculative micro-optimizations unless the code path is clearly performance-sensitive.

## Testing Guidance

Tests use `xunit.v3`, `Shouldly`, and `NSubstitute`.

Global test usings are configured in `Directory.Build.targets`, so `Xunit`, `Shouldly`, and `NSubstitute` are already available.

Preferred test naming:

- `MethodName_Condition_ExpectedResult`

When adding or changing behavior:

- update or add focused unit tests in the nearest test project
- keep assertions in `Shouldly`
- avoid introducing new test libraries unless necessary

Common test gotchas:

- Warnings are errors: use `ShouldNotBeNull()` or `!` before asserting on `string?` / nullable properties
- `SecretValue.ToString()` returns `"***REDACTED***"` — use `.DecryptToString()` for actual values
- NSubstitute cannot mock `ILogger<T>` for `internal` types — use `NullLogger<T>.Instance`
- `AITool` cannot be mocked — create a concrete stub that overrides `Name`
- HTTP connectors accept `HttpClient` via constructor — use `StubHandler : HttpMessageHandler` for test isolation
- `private static` helpers on actors should be `internal static` for direct testing (pattern: `ProofValidatorActor`)

## Common Change Patterns

### Add a new actor

1. Add the domain interface in `Actors/` of the appropriate domain project.
2. Add the implementation in the same project.
3. Add the Orleans grain bridge in `Weave.Silo/VirtualActors/GrainInterfaces.cs`.
4. Add or extend the state model in `Models/`.
5. Add commands, queries, or events if the actor is externally driven.
6. Register any required services in `src/Runtime/Weave.Silo/Program.cs`.
7. Add unit tests in the matching test project.

### Add a new tool connector

1. Implement `IToolConnector` in `Weave.Tools/Connectors/`.
2. Extend `ToolType` when required.
3. Update discovery and Silo registrations.
4. Extend the workspace manifest model if the connector needs new config.
5. Add tests in `src/Tools/Weave.Tools.Tests/`.

### Add a new deploy publisher

1. Implement `IPublisher` in `Weave.Deploy/Translators/`.
2. Wire the target into `src/UX/Weave.Cli/Commands/PublishCommand.cs`.
3. Add tests in `src/Deployment/Weave.Deploy.Tests/`.

## CLI UX Philosophy

Every CLI command must work in two modes:

### Guided mode (zero arguments)
When a user runs a command with no arguments, the CLI asks questions to fill in the gaps. Missing info is expected — resolve it by prompting, not by erroring. Help the user make the best choices with sensible defaults and clear descriptions.

```bash
# All of these should work with zero arguments:
weave run                    # detect workspace or show list to pick from
weave workspace new          # ask for name, pick preset interactively
weave data export            # list workspaces, let user pick
weave workspace up           # detect or pick workspace
weave storage change         # pick backend, prompt for connection
weave marketplace submit     # walk through name, description, category
```

### Advanced mode (all arguments)
Power users and scripts pass all arguments to skip prompts:

```bash
weave run my-app --port 8080
weave workspace new my-app --preset coding-assistant
weave data export my-app -o backup.json
weave storage change postgresql --connection "Host=..."
```

### Rules for new commands

1. All positional arguments must be optional (`Arity = ArgumentArity.ZeroOrOne`)
2. When a required value is missing, prompt for it — never print "error: missing argument"
3. Selection prompts (`SelectionPrompt`) for lists, text prompts for free-form input
4. Always provide sensible defaults in prompts (`.DefaultValue(...)`)
5. When an operation might conflict (e.g. database already exists), offer choices: stop, override, or fix
6. Use `CliTheme.WriteInfo/WriteSuccess/WriteWarning` for feedback, not raw `Console.WriteLine`
7. Show next steps after completion (`CliTheme.WriteMuted`)

## Default Naming Conventions

All defaults must be namespaced to avoid collisions with common applications:

| Resource | Default | Rationale |
|----------|---------|-----------|
| HTTP port | `9401` | 94xx range avoids ASP.NET 5000, Node 3000, K8s 30000+ |
| Database name | `weave` | Distinctive enough, user provides their own credentials |
| SQLite file | `~/.weave/weave.db` | Namespaced in `.weave/` directory |
| Network name | `weave-{name}` | Prefixed to avoid Docker/Podman collisions |
| Config directory | `~/.weave/` | Dot-prefixed, unique name |

Never invent default credentials (usernames, passwords, tokens). Connection strings should template the structure with empty credential fields (`Username=;Password=`) so the user fills in their own. Avoid generic names like `app`, `data`, `default`, `main` for resources we name.

## Security Notes

- Never add secrets to source control.
- Capability tokens gate tool access.
- Tool input and output may be scanned for leaks.
- Keep redaction and fail-closed behavior intact when changing security-sensitive flows.
