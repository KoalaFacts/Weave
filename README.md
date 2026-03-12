# Weave

Weave is a .NET 10 workspace orchestration prototype for AI agents. It combines Orleans grains, a manifest-driven workspace model, a CLI, an HTTP API, and a Blazor dashboard so a single `workspace.yml` can describe agents, tools, hooks, secrets, and deployment targets.

## Current Scope

The repository currently includes:

- manifest parsing and validation in `Weave.Workspaces`
- workspace startup and teardown through an Orleans-backed API in `Weave.Silo`
- a local Podman runtime for workspace networking and MCP-style tool containers
- agent lifecycle, chat, task, heartbeat, and tool-registry grains in `Weave.Agents`
- tool connectors for MCP, CLI, OpenAPI, and optional Dapr wiring in `Weave.Tools`
- security primitives for capability tokens, leak scanning, and secret substitution in `Weave.Security`
- deployment manifest generation for `docker-compose`, `kubernetes`, `nomad`, `fly-io`, and `github-actions`

## Quick Start

```bash
# Build and test
dotnet build Weave.slnx
dotnet test Weave.slnx

# Start the local stack (Redis + Orleans silo + dashboard)
dotnet run --project src/Weave.AppHost

# Create a workspace scaffold
dotnet run --project src/Weave.Cli -- workspace new demo

# Validate the manifest
dotnet run --project src/Weave.Cli -- config validate --workspace demo

# Start the workspace through the Silo API
dotnet run --project src/Weave.Cli -- up --workspace demo

# Inspect live state
dotnet run --project src/Weave.Cli -- status --workspace demo

# Generate deployment manifests
dotnet run --project src/Weave.Cli -- publish kubernetes --workspace demo

# Stop the workspace
dotnet run --project src/Weave.Cli -- down --workspace demo
```

The CLI talks to `Weave.Silo` over HTTP. By default it uses `http://localhost:52036`; override with `WEAVE_API_URL` if needed.

## Architecture

```
          ┌───────────────────────┐
          │    Weave.AppHost      │
          │ Aspire local topology │
          └───────────┬───────────┘
                      │
        ┌─────────────┴─────────────┐
        │                           │
┌───────▼────────┐         ┌────────▼────────┐
│   Weave.Silo   │         │ Weave.Dashboard │
│ Orleans + API  │         │ Blazor UI       │
└───┬─────────┬──┘         └─────────────────┘
    │         │
    │         └──────────────────────────────┐
    │                                        │
┌───▼────────────┐  ┌────────────────┐  ┌────▼────────────┐
│Weave.Workspaces│  │ Weave.Agents   │  │ Weave.Tools     │
│manifest/runtime│  │ grains/pipeline│  │ connectors/grains│
└───┬────────────┘  └──────┬─────────┘  └────┬────────────┘
    │                      │                 │
    └──────────────┬───────┴────────────┬────┘
                   │                    │
             ┌─────▼─────┐        ┌────▼─────┐
             │Weave.Shared│        │Weave.Security│
             │IDs/CQRS/etc│        │tokens/scanning│
             └────────────┘        └──────────────┘
```

## Project Structure

```
src/
  Weave.Shared/            Shared kernel: branded IDs, CQRS, events, lifecycle
  Weave.ServiceDefaults/   Aspire defaults and observability setup
  Weave.Workspaces/        Manifest model, parser, runtime abstraction, workspace grains
  Weave.Agents/            Agent grains, supervisor, heartbeat, chat pipeline
  Weave.Security/          Capability tokens, leak scanner, secret proxy, secret providers
  Weave.Tools/             Tool connectors, discovery, tool grains
  Weave.Deploy/            Deployment manifest publishers
  Weave.Dashboard/         Blazor Server dashboard
  Weave.Cli/               Spectre.Console CLI for local workspace operations
  Weave.Silo/              Orleans host and HTTP endpoints
  Weave.AppHost/           Aspire app host for local orchestration
  Weave.SourceGen/         `netstandard2.0` source generator for branded IDs

tests/
  Weave.Workspaces.Tests/
  Weave.Agents.Tests/
  Weave.Security.Tests/
  Weave.Tools.Tests/
  Weave.Deploy.Tests/
```

## Workspace Manifest Example

```yaml
version: "1.0"
name: demo

workspace:
  isolation: full
  network:
    name: weave-demo
  secrets:
    provider: env

agents:
  assistant:
    model: claude-sonnet-4-20250514
    system_prompt_file: ./prompts/assistant.md
    max_concurrent_tasks: 3
    tools: [git]
    capabilities: [tool:*]
    heartbeat:
      cron: "0 * * * *"
      tasks:
        - Review repository status

tools:
  git:
    type: cli
    cli:
      shell: /bin/bash
      allowed_commands:
        - git status
        - git diff --stat

targets:
  local:
    runtime: podman
  ci:
    runtime: github-actions
    trigger: pull_request
```

## CLI Commands

```text
weave workspace new <name>     Create a workspace scaffold under `workspaces/<name>`
weave workspace list           List workspace folders
weave workspace remove <name>  Remove a workspace registration (`--purge` deletes files)

weave up --workspace <name>    Start a workspace through the Silo API
weave down --workspace <name>  Stop a running workspace
weave status                   Show manifest data or live workspace state

weave publish <target>         Generate deployment manifests
weave config show              Print `workspace.yml`
weave config validate          Parse and validate `workspace.yml`
```

## Notes on Runtime Behavior

- `Weave.AppHost` is the easiest local entry point for development.
- `Weave.Silo` registers MCP, CLI, and OpenAPI connectors by default.
- Dapr support is enabled when `DAPR_HTTP_PORT` is present.
- The current `IWorkspaceRuntime` implementation is `PodmanRuntime`.
- Deployment publishers generate manifests; they do not deploy infrastructure directly.

## Tech Stack

| Component | Technology |
|-----------|------------|
| Runtime | .NET 10 |
| Source generator target | .NET Standard 2.0 |
| Actor framework | Microsoft Orleans 10.0.1 |
| App orchestration | .NET Aspire 13.1.2 |
| LLM abstraction | Microsoft.Extensions.AI 10.4.0 |
| Dapr integration | Dapr .NET SDK 1.17.3 |
| CLI | Spectre.Console 0.54.0 / Spectre.Console.Cli 0.53.1 |
| Dashboard | Blazor Server + Fluent UI 4.14.0 |
| Manifest parsing | YamlDotNet 16.3.0 |
| Secret provider integration | VaultSharp 1.17.5.1 |
| Testing | xUnit v3 + Shouldly + NSubstitute |

## License

See `LICENSE`.
