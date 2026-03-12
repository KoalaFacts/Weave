# Weave — Development Guide

## Build and Test

```bash
dotnet build Weave.slnx
dotnet test Weave.slnx

dotnet test tests/Weave.Workspaces.Tests
dotnet test tests/Weave.Agents.Tests
dotnet test tests/Weave.Security.Tests
dotnet test tests/Weave.Tools.Tests
dotnet test tests/Weave.Deploy.Tests
```

Most projects target `net10.0`. `Weave.SourceGen` targets `netstandard2.0`.

The solution uses central package management through `Directory.Packages.props`. Add package versions there unless the project already opts out, such as `Weave.SourceGen`.

## Repository Rules

- Warnings are treated as errors.
- Prefer minimal, targeted changes.
- Match the existing code style in the touched project.
- Keep the current dependency flow; do not introduce circular references.
- Do not use `FluentAssertions`. Tests use `Shouldly`.

## Project Layout

```text
src/
  Weave.Shared/          Shared abstractions, branded IDs, CQRS, events, lifecycle
  Weave.Workspaces/      Manifest parsing, workspace state, runtime abstraction
  Weave.Agents/          Agent grains, supervisor, heartbeat, chat pipeline
  Weave.Security/        Capability tokens, leak scanning, secret proxy, providers
  Weave.Tools/           Tool connectors, discovery, tool grain
  Weave.Deploy/          Deployment publishers
  Weave.Cli/             Spectre.Console CLI
  Weave.Silo/            Orleans host and HTTP API
  Weave.Dashboard/       Blazor dashboard
  Weave.AppHost/         Aspire local orchestration host
  Weave.SourceGen/       Branded ID source generator (`netstandard2.0`)
```

Dependency flow should stay roughly:

`Shared -> Workspaces -> Agents/Tools/Security/Deploy -> Silo/Cli/Dashboard -> AppHost`

## Architecture Conventions

### Orleans

- Grain interfaces live separately from grain implementations.
- Grain keys are string-based.
- Common key shapes:
  - workspace: `{workspaceId}`
  - agent: `{workspaceId}/{agentName}`
  - tool: `{workspaceId}/{toolName}`
  - heartbeat: `{workspaceId}/{agentName}`
- Grain state models use `[GenerateSerializer]` and `[Id(n)]`.
- Tests may instantiate grains directly, so implementations should not rely exclusively on `OnActivateAsync` for safe defaults.

### CQRS and API Flow

- Commands and queries live with their domain.
- `Weave.Silo` wires handlers through `AddCqrs(...)`.
- HTTP endpoints in `src/Weave.Silo/Api` are thin adapters over CQRS dispatch.

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

### Performance

- Prefer straightforward code first.
- Follow existing low-allocation patterns in hot paths.
- Avoid speculative micro-optimizations unless the code path is clearly performance-sensitive.

## Testing Guidance

Tests use `xunit.v3`, `Shouldly`, and `NSubstitute`.

Global test usings are configured in `tests/Directory.Build.props`, so `Xunit`, `Shouldly`, and `NSubstitute` are already available.

Preferred test naming:

- `MethodName_Condition_ExpectedResult`

When adding or changing behavior:

- update or add focused unit tests in the nearest test project
- keep assertions in `Shouldly`
- avoid introducing new test libraries unless necessary

## Common Change Patterns

### Add a new grain

1. Add the interface in `Grains/`.
2. Add the implementation in `Grains/`.
3. Add or extend the state model in `Models/`.
4. Add commands, queries, or events if the grain is externally driven.
5. Register any required services in `src/Weave.Silo/Program.cs`.
6. Add unit tests in the matching test project.

### Add a new tool connector

1. Implement `IToolConnector` in `Weave.Tools/Connectors/`.
2. Extend `ToolType` when required.
3. Update discovery and Silo registrations.
4. Extend the workspace manifest model if the connector needs new config.
5. Add tests in `tests/Weave.Tools.Tests/`.

### Add a new deploy publisher

1. Implement `IPublisher` in `Weave.Deploy/Translators/`.
2. Wire the target into `src/Weave.Cli/Commands/PublishCommand.cs`.
3. Add tests in `tests/Weave.Deploy.Tests/`.

## Security Notes

- Never add secrets to source control.
- Capability tokens gate tool access.
- Tool input and output may be scanned for leaks.
- Keep redaction and fail-closed behavior intact when changing security-sensitive flows.
