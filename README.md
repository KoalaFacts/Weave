# Weave

**AI Agent Workspace Orchestrator** — Orleans virtual actors own the agent brain, Dapr is the polyglot tool connector, and a single `workspace.yml` manifest defines fully-isolated workspaces that translate to Docker Compose, Kubernetes, Nomad, Fly.io, or GitHub Actions.

## Quick Start

```bash
# Build
dotnet build

# Run tests
dotnet test

# Create a workspace
dotnet run --project src/Weave.Cli -- workspace new my-workspace

# Start the workspace
dotnet run --project src/Weave.Cli -- up --workspace my-workspace

# Generate deployment manifests
dotnet run --project src/Weave.Cli -- publish kubernetes --workspace my-workspace

# Start the Aspire orchestrator (cluster mode)
dotnet run --project src/Weave.AppHost
```

## Architecture

```
                    ┌─────────────────────────────────┐
                    │         Weave.AppHost            │
                    │   (Aspire orchestrator: Orleans  │
                    │    cluster, Redis, Dapr, etc.)   │
                    └────────────┬────────────────────┘
                                 │
              ┌──────────────────┼──────────────────┐
              │                  │                   │
     ┌────────▼───────┐  ┌──────▼───────┐  ┌───────▼────────┐
     │   Weave.Silo   │  │  Dashboard   │  │   Weave.Cli    │
     │ (Orleans host + │  │ (Blazor +    │  │ (Spectre.Console│
     │  API endpoints) │  │  FluentUI)   │  │  AOT binary)   │
     └───┬───┬───┬────┘  └──────────────┘  └────────────────┘
         │   │   │
    ┌────┘   │   └────┐
    │        │        │
┌───▼──┐ ┌──▼───┐ ┌──▼────┐
│Agents│ │Tools │ │Security│    ← Feature slices (Orleans grains)
└──┬───┘ └──┬───┘ └──┬────┘
   │        │        │
┌──▼────────▼────────▼──┐
│     Weave.Workspaces   │    ← Workspace domain (manifest, runtime)
└───────────┬────────────┘
            │
     ┌──────▼──────┐
     │ Weave.Shared │    ← Shared kernel (IDs, events, CQRS, lifecycle)
     └─────────────┘
```

## Project Structure

```
src/
  Weave.Shared/            Shared kernel — branded IDs, CQRS, events, lifecycle
  Weave.ServiceDefaults/   Aspire service defaults (OpenTelemetry, health checks)
  Weave.Workspaces/        Workspace domain — manifest parsing, runtime provisioning
  Weave.Agents/            Agent grains, heartbeat system, IChatClient pipeline
  Weave.Security/          Capability tokens, leak scanner, secret proxy, Vault
  Weave.Tools/             Tool connectors (MCP, Dapr, CLI, OpenAPI), tool grains
  Weave.Deploy/            Publishers for Docker Compose, K8s, Nomad, Fly.io, GH Actions
  Weave.Dashboard/         Blazor Server + FluentUI web management UI
  Weave.Cli/               Spectre.Console CLI — workspace/up/down/status/publish/config
  Weave.Silo/              Orleans silo host with HTTP API endpoints
  Weave.AppHost/           Aspire orchestrator (Redis, Orleans clustering, Dapr)
  Weave.SourceGen/         Branded ID source generator

tests/
  Weave.Workspaces.Tests/  Manifest parsing, workspace lifecycle
  Weave.Agents.Tests/      Agent grain, task handling, tool registry
  Weave.Security.Tests/    Capability tokens, leak scanner, secret proxy
  Weave.Tools.Tests/       Tool grain connect/invoke/leak blocking
  Weave.Deploy.Tests/      Publisher output validation
```

## Workspace Manifest (`workspace.yml`)

```yaml
version: "1.0"
name: my-agent-workspace

workspace:
  isolation: full
  network:
    name: weave-my-agent-workspace
  secrets:
    provider: vault
    vault:
      address: https://vault.example.com

agents:
  researcher:
    model: claude-sonnet-4-20250514
    system_prompt_file: ./prompts/researcher.md
    max_concurrent_tasks: 5
    tools: [web-search, github-api]
    heartbeat:
      cron: "*/30 * * * *"
      tasks:
        - Check for new research papers

tools:
  web-search:
    type: mcp
    mcp:
      server: npx
      args: ["-y", "@anthropic/mcp-server-web-search"]
  github-api:
    type: openapi
    openapi:
      spec_url: https://api.github.com/openapi.yaml

targets:
  local:    { runtime: podman }
  staging:  { runtime: k3s, replicas: 2 }
  ci:       { runtime: github-actions, trigger: pull_request }
```

## CLI Commands

```
weave workspace new <name>     Create workspace folder + scaffold manifest
weave workspace list           List all workspaces
weave workspace remove <name>  Deregister workspace (--purge to delete)

weave up [--target=local]      Start workspace
weave down                     Stop workspace
weave status                   Show workspace/agents/tools

weave publish <target>         Generate deploy manifests
weave config show              Show workspace configuration
weave config validate          Validate workspace manifest
```

## Key Design Decisions

- **Orleans virtual actors** — each agent, tool, workspace, and heartbeat is an Orleans grain with persistent state and single-threaded execution
- **Dapr sidecar** — polyglot tool connectivity via service invocation, pub/sub for cross-service events
- **IChatClient middleware** — all LLM calls go through a composable pipeline (Microsoft.Extensions.AI) with cost tracking and rate limiting
- **Capability-based security** — HMAC-SHA256 signed tokens with grants (`tool:*`, `secret:*`), expiry, and revocation
- **Defense-in-depth leak scanning** — 15+ regex patterns + Shannon entropy analysis on both requests and responses
- **Fail-closed sandbox** — if sandbox mode is configured but not active, tool execution throws
- **Heartbeat system** — cron-triggered proactive agent wake-up (inspired by OpenClaw)
- **Multi-target deploy** — single manifest translates to Docker Compose, Kubernetes, Nomad, Fly.io, or GitHub Actions

## Tech Stack

| Component | Technology |
|-----------|-----------|
| Runtime | .NET 10, C# 13 |
| Actor framework | Microsoft Orleans 9.1 |
| Orchestration | .NET Aspire 13.1 |
| Tool connectivity | Dapr 1.15 |
| LLM abstraction | Microsoft.Extensions.AI 9.5 |
| CLI | Spectre.Console 0.50 |
| Dashboard | Blazor Server + FluentUI 4.14 |
| Manifest parsing | YamlDotNet 16.3 |
| Secret management | HashiCorp Vault (VaultSharp) |
| Testing | xunit + FluentAssertions + NSubstitute |

## License

See LICENSE file.
