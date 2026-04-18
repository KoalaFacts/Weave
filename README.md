# Weave

[![CI](https://github.com/KoalaFacts/Weave/actions/workflows/ci.yml/badge.svg)](https://github.com/KoalaFacts/Weave/actions/workflows/ci.yml)
[![Security Scan](https://github.com/KoalaFacts/Weave/actions/workflows/scan-security.yml/badge.svg)](https://github.com/KoalaFacts/Weave/actions/workflows/scan-security.yml)

**Security-first AI agent orchestration for .NET.** Define assistants, lock down their tools with capability tokens, and keep secrets from leaking — all from a single manifest file. Runs locally with one command.

Most agent frameworks give your AI unrestricted access and hope for the best. Weave starts from the opposite assumption: assistants get *only* the capabilities you explicitly grant, secrets are proxied and scanned for leaks, and every tool call goes through a security boundary you control.

## What You Can Build

- **A coding assistant** that has git and filesystem access but can't `rm -rf /` or `git push --force`
- **A research team** with a supervisor that delegates to specialist workers, each with their own tools
- **A support bot** on Slack, Discord, or Telegram that remembers each user's preferences and gets better at recurring tasks
- **A DevOps agent** that deploys services on a cron schedule and builds reusable skills from successful deployments
- **A shared tool marketplace** where your team publishes vetted tool configurations that any workspace can install

## How Weave Is Different

| | Other agent frameworks | Weave |
|---|---|---|
| **Security model** | Bolt-on, if any | Capability tokens, secret proxying, leak scanning, sandboxed tools |
| **Secret handling** | Pass API keys directly to the model | Secrets never reach the model — proxied, redacted, and scanned for 15+ leak patterns |
| **Tool access** | Allow-all or manual prompt engineering | Sandboxed filesystem, shell metacharacter blocking, SSRF protection, allow/deny lists |
| **Self-improving** | Static prompts | Agents auto-extract skills from completed tasks and retrieve them for similar future work |
| **Reach users** | API-only | Built-in channel gateway — Slack, Discord, Telegram, Teams, Email out of the box |
| **Personalization** | Stateless | User modeling tracks preferences, topics, and context across sessions |
| **Architecture** | Single-process, in-memory | Orleans grain-based — each agent, tool, and workspace is an independent, recoverable actor |
| **Configuration** | Code-heavy setup | One JSONC manifest defines everything |
| **Extensibility** | Rebuild to add integrations | Hot-swap plugins at runtime (Dapr, Vault, webhooks) without restarts |
| **Runtime** | Cloud-only or single-machine | Local-first, scales to Kubernetes when you need it |

## Quick Start

Weave runs on Windows, macOS, and Linux.

**Install:**

| Platform | Command |
|----------|--------|
| **Windows** | `irm https://raw.githubusercontent.com/KoalaFacts/Weave/main/scripts/install.ps1 \| iex` |
| **macOS / Linux** | `curl -fsSL https://raw.githubusercontent.com/KoalaFacts/Weave/main/scripts/install.sh \| sh` |
| **.NET** | `dotnet tool install --global Weave.Cli` |

**Create and run your first workspace:**

```bash
weave workspace new demo --preset coding-assistant
weave run demo
```

That is it. `weave run` starts the server and workspace in one command. Everything runs locally — no external services required.

**Or try the support-team preset** with Slack, skill memory, and a health monitor:

```bash
weave workspace new my-team --preset support-team
weave run my-team
```

## Example Manifest

A workspace manifest is a JSONC file. This one defines an assistant with git access and sandboxed file operations:

```jsonc
{
  "version": "1.0",
  "name": "my-workspace",
  "agents": {
    "coder": {
      "model": "claude-sonnet-4-20250514",
      "tools": ["git", "files"],
      "max_concurrent_tasks": 3
    }
  },
  "tools": {
    "git": {
      "type": "cli",
      "cli": {
        "shell": "/bin/bash",
        "allowed_commands": ["git *"],
        "denied_commands": ["git push --force", "git reset --hard"]
      }
    },
    "files": {
      "type": "filesystem",
      "filesystem": {
        "root": "./workspace-data",
        "sandbox": true,
        "read_only": false
      }
    }
  }
}
```

See the full schema in the [Manifest Reference](docs/manifest-reference.md).

## Channels — Reach Users Where They Are

Connect your agents to messaging platforms. Users talk to agents on Slack, Discord, or Telegram — Weave routes messages to the right agent and sends responses back.

```jsonc
"channels": {
  "slack-support": {
    "type": "slack",
    "target_agent": "support-bot",
    "config": {
      "webhook_url": "https://hooks.slack.com/services/..."
    }
  },
  "telegram-alerts": {
    "type": "telegram",
    "target_agent": "monitor",
    "config": {
      "bot_token": "${secrets.telegram_bot_token}",
      "chat_id": "-100123456789"
    }
  }
}
```

Supported channels: **Slack**, **Discord**, **Telegram**, **Microsoft Teams**, **Email**. Each channel routes to a specific agent, or you can configure pattern-based routing rules via the API.

Inbound messages are received via webhook at `POST /api/workspaces/{id}/channels/inbound`. The channel gateway resolves the target agent, forwards the message, and returns the response.

## Skill Memory — Agents That Learn

When an agent completes a multi-step task and it gets accepted, Weave automatically extracts a **skill document** capturing what was done, which tools were used, and in what order. The next time a similar request comes in, the agent retrieves matching skills and includes them in its context.

This means your agents get measurably better at recurring tasks without any manual prompt engineering.

```bash
# Skills are created automatically, but you can also store them manually:
curl -X POST http://localhost:5000/api/workspaces/my-ws/skills \
  -H "Content-Type: application/json" \
  -d '{
    "title": "Deploy to staging",
    "description": "Build, test, and deploy a service to the staging environment",
    "tags": ["deploy", "staging", "docker"],
    "steps": [
      { "action": "Run tests", "tool_name": "cli" },
      { "action": "Build Docker image", "tool_name": "cli" },
      { "action": "Push to registry", "tool_name": "cli" },
      { "action": "Update deployment manifest", "tool_name": "files" }
    ],
    "tools_used": ["cli", "files"],
    "created_by_agent": "deployer"
  }'

# Search for relevant skills:
curl http://localhost:5000/api/workspaces/my-ws/skills/search?q=deploy+staging
```

Skills are scoped to a workspace — all agents in the workspace share and benefit from accumulated skills.

## User Modeling — Personalized Agents

Weave tracks user preferences, frequently discussed topics, and domain context across sessions. When a user sends a message (via a channel or the API with a `userId`), their profile is automatically injected into the agent's context.

```bash
# Set user preferences
curl -X PUT http://localhost:5000/api/workspaces/my-ws/users/alice/preferences \
  -H "Content-Type: application/json" \
  -d '{ "key": "language", "value": "python" }'

# Set domain context
curl -X PUT http://localhost:5000/api/workspaces/my-ws/users/alice/context \
  -H "Content-Type: application/json" \
  -d '{ "key": "project", "value": "data-pipeline-v2" }'

# View profile
curl http://localhost:5000/api/workspaces/my-ws/users/alice/profile
```

When Alice messages the agent, it automatically receives: *"User preferences: language=python. Top topics: deployment (12), testing (8). Domain context: project=data-pipeline-v2."*

## Marketplace — Share Vetted Tool Configurations

A curated registry of tool configurations that teams can share. Unlike unrestricted plugin marketplaces, every item must pass a security review before publishing.

```bash
# Submit a tool configuration
curl -X POST http://localhost:5000/api/marketplace \
  -H "Content-Type: application/json" \
  -d '{
    "name": "GitHub MCP",
    "description": "GitHub integration via MCP server",
    "category": "ToolConnector",
    "version": "1.0.0",
    "author": "platform-team",
    "tags": ["github", "mcp", "git"]
  }'

# Publish after security review
curl -X POST http://localhost:5000/api/marketplace/{itemId}/publish \
  -d '{ "reviewer_id": "security-lead", "approved": true, "notes": "Reviewed, safe to use" }'

# Search and browse
curl http://localhost:5000/api/marketplace/search?q=github
curl http://localhost:5000/api/marketplace
```

## Capability Templates — Pre-Built Agent Configurations

Shareable, versioned agent configurations with pre-validated tool chains. Create a template once, instantiate it across workspaces.

```bash
# Register a template
curl -X POST http://localhost:5000/api/templates \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Code Reviewer",
    "description": "Agent configured for PR reviews with git and file access",
    "version": "1.0.0",
    "author": "platform-team",
    "agent_definition": {
      "model": "claude-sonnet-4-20250514",
      "tools": ["git", "files"],
      "max_concurrent_tasks": 3
    },
    "required_tools": {
      "git": { "type": "cli", "cli": { "shell": "/bin/bash", "allowed_commands": ["git *"] } },
      "files": { "type": "filesystem", "filesystem": { "root": "./repo", "sandbox": true } }
    },
    "tags": ["code-review", "git"]
  }'

# Validate and publish
curl -X POST http://localhost:5000/api/templates/{templateId}/publish

# Browse published templates
curl http://localhost:5000/api/templates
```

Templates validate that the agent model is specified, all referenced tools exist in `required_tools`, and every tool has a valid type — before they can be published.

## Storage Backends

Weave uses Orleans grain persistence. Choose the backend that fits your infrastructure:

| Backend | Config value | Connection string key | Use case |
|---|---|---|---|
| **In-Memory** | `memory` (default) | — | Instant start, resets on restart |
| **SQLite** | `sqlite` | `ConnectionStrings:Sqlite` | Zero-config local file, persists across restarts |
| **Redis** | `redis` | `ConnectionStrings:Redis` | Fast, shared state across silos |
| **SQL Server** | `sqlserver` | `ConnectionStrings:SqlServer` | Enterprise, existing SQL infrastructure |
| **PostgreSQL** | `postgresql` | `ConnectionStrings:PostgreSql` | Cross-platform relational, open source |

Configure via `appsettings.json` or environment variables:

```jsonc
{
  "Weave": {
    "Storage": "postgresql"  // or "sqlserver", "redis", "memory"
  },
  "ConnectionStrings": {
    "PostgreSql": "Host=localhost;Database=weave;Username=weave;Password=secret"
  }
}
```

Or change the global default via CLI:
```bash
weave storage change postgresql --connection "Host=localhost;Database=weave;Username=weave;Password=secret"
```

Or override per-workspace in the manifest:
```jsonc
"workspace": {
  "storage": {
    "backend": "postgresql",
    "connection_string": "Host=db.prod.example.com;Database=weave;..."
  }
}
```

Workspace-level storage overrides take precedence over the global setting. This lets you run dev workspaces in memory while production workspaces persist to PostgreSQL.

All backends support both grain state persistence and cluster membership. Orleans provides [SQL scripts](https://learn.microsoft.com/dotnet/orleans/host/configuration-guide/adonet-configuration) for creating the required tables.

### Migrating between backends

When you switch storage (e.g., SQLite locally to PostgreSQL in production), use `weave data export` to create a portable snapshot and `weave data import` to restore it on the new backend:

```bash
# 1. Export while current backend is running
weave data export my-app -o backup.json

# 2. Stop the server
weave workspace down my-app

# 3. Switch backend
weave storage change postgresql --connection "Host=localhost;Database=weave;..."

# 4. Import into the new backend
weave run my-app
weave data import backup.json
```

The export includes the workspace manifest, prompt files, skills, channels, user profiles, marketplace items, and templates — everything needed to fully reconstruct a workspace on a different machine or backend.

## Security

Security is not an add-on — it is the architecture. Every tool call passes through multiple layers before anything executes.

### Capability tokens

Agents receive time-limited, scoped tokens that grant access to specific tools. A token for `tool:git` cannot invoke `tool:files`. Tokens are HMAC-SHA256 signed with constant-time verification, and can be revoked at any time.

### Secret management

Secrets never reach the AI model. When a manifest references `${secrets.api_key}`, Weave's secret proxy resolves the value at the grain boundary. The model only ever sees the placeholder. Responses are scanned for 15+ leak patterns (AWS keys, GitHub tokens, JWTs, private keys, connection strings) plus Shannon entropy analysis — if a secret leaks into a tool response, it is redacted before the agent sees it.

### Sandboxed filesystem

The filesystem connector locks all operations to a configured root directory:

- Path traversal blocked (`..`, absolute paths, drive letters, URL schemes, null bytes)
- NTFS Alternate Data Streams blocked
- Symlink/junction escape prevention — every path component resolved and verified
- Read-only mode, configurable size limits
- 7 operations: `read_file`, `write_file`, `edit_file`, `list_directory`, `search_files`, `grep`, `file_info`

### Tool-level protections

- **CLI**: Shell metacharacter injection blocked (`;`, `|`, `&&`, `` ` ``, `$()`). Allow/deny wildcard patterns with case-insensitive matching.
- **HTTP** (OpenAPI / DirectHttp): SSRF protection rejects path traversal, encoded characters, and absolute URL injection.
- **MCP**: Process isolation for external servers.

## Built-in Tools

| Type | What it does |
|------|-------------|
| **filesystem** | Sandboxed file access — read, write, edit, grep, search. Locked to a root directory with symlink escape prevention. |
| **cli** | Shell commands with allow/deny lists and metacharacter blocking. |
| **mcp** | Model Context Protocol servers over stdin/stdout. |
| **openapi** | HTTP APIs described by an OpenAPI spec, with SSRF protection. |
| **direct_http** | Lightweight HTTP calls to a base URL with path validation. |
| **dapr** | Dapr service invocation through the sidecar. |

## Plugins

Plugins swap runtime services without rebuilding or restarting. They activate based on environment detection or explicit manifest configuration.

| Plugin | What it provides |
|--------|-----------------|
| **dapr** | Event bus and tool connector via Dapr sidecar |
| **vault** | Secret provider backed by HashiCorp Vault |
| **webhook** | Event bus that posts domain events to a URL |
| **http** | Named HTTP clients for custom integrations |

```jsonc
"plugins": {
  "vault": {
    "type": "vault",
    "config": {
      "address": "https://vault.example.com"
      // token resolved from VAULT_TOKEN env var
    }
  }
}
```

## Presets

| Preset | What you get |
|--------|-------------|
| **starter** | One assistant, no tools — the simplest possible workspace. |
| **coding-assistant** | An assistant with git and filesystem tools, ready for code tasks. |
| **research** | An assistant with web and document tools for gathering information. |
| **multi-agent** | A supervisor and worker assistants for more complex workflows. |

```bash
weave workspace new demo --preset coding-assistant
```

## CLI

```text
weave workspace new <name>          Create a new workspace
weave workspace up <name>           Start a workspace
weave workspace down <name>         Stop a workspace
weave workspace status <name>       See what is happening

weave workspace add agent <name>    Add an assistant
weave workspace add tool <name>     Add a tool
weave workspace add target <name>   Add a deployment target

weave workspace show <name>         Show the current configuration
weave workspace validate <name>     Check that everything is correct
weave workspace publish <name>      Generate deployment files
weave workspace presets             Browse preset templates

weave workspace list                List all workspaces
weave workspace remove <name>       Remove a workspace

weave storage show                  Show current storage backend
weave storage change <backend>      Switch storage (stop server first)

weave data export <name>            Export workspace to portable JSON
weave data import <file>            Import workspace from export file

weave marketplace list              Browse published marketplace items
weave marketplace search <query>    Search the marketplace
weave marketplace submit            Submit a new tool configuration
weave marketplace publish <id>      Publish after security review
weave marketplace info <id>         Show item details
```

## Architecture

Weave is built on [Orleans](https://learn.microsoft.com/dotnet/orleans/) — every agent, tool, workspace, and security boundary is an independent grain that can fail and recover without taking down the system.

```text
Workspace Manifest (JSONC)
    |
    v
Silo (Orleans Host + ASP.NET Core APIs)
    |
    +-- Agent Grains (AI model integration, task management, chat pipeline)
    +-- Tool Grains (connector dispatch, token validation, leak scanning)
    +-- Security Grains (secret proxy, capability tokens)
    +-- Plugin Service Broker (hot-swap Dapr, Vault, webhooks at runtime)
    |
    v
Tool Connectors (FileSystem, CLI, MCP, OpenAPI, DirectHttp, Dapr)
```

The dashboard provides a live view of workspace status, agent activity, tool connections, and LLM costs.

## Documentation

- [Manifest Reference](docs/manifest-reference.md) — full schema for workspace JSONC files, including channels
- [Tools](docs/tools.md) — connector interfaces, security features, and testing patterns
- [Examples](docs/examples.md) — end-to-end workspace manifests, CLI usage, and code patterns
- [Architecture docs](docs/) — subsystem documentation for Foundation, Workspaces, Assistants, Tools, Security, Deployment, and Runtime

## Built With

- [.NET 10](https://dotnet.microsoft.com/) and [Orleans](https://learn.microsoft.com/dotnet/orleans/) for the actor-based runtime
- [Microsoft.Extensions.AI](https://devblogs.microsoft.com/dotnet/introducing-microsoft-extensions-ai-preview/) for model-agnostic AI integration
- [Aspire](https://learn.microsoft.com/dotnet/aspire/) for local orchestration and observability
- [Spectre.Console](https://spectreconsole.net/) for the interactive CLI

## License

Licensed under either of [Apache License, Version 2.0](LICENSE-APACHE) or [MIT License](LICENSE-MIT), at your option.
