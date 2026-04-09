# Weave

[![CI](https://github.com/KoalaFacts/Weave/actions/workflows/ci.yml/badge.svg)](https://github.com/KoalaFacts/Weave/actions/workflows/ci.yml)
[![Security Scan](https://github.com/KoalaFacts/Weave/actions/workflows/scan-security.yml/badge.svg)](https://github.com/KoalaFacts/Weave/actions/workflows/scan-security.yml)

**Security-first AI agent orchestration for .NET.** Define assistants, lock down their tools with capability tokens, and keep secrets from leaking — all from a single manifest file. Runs locally with one command.

Most agent frameworks give your AI unrestricted access and hope for the best. Weave starts from the opposite assumption: assistants get *only* the capabilities you explicitly grant, secrets are proxied and scanned for leaks, and every tool call goes through a security boundary you control.

## How Weave Is Different

| | Other agent frameworks | Weave |
|---|---|---|
| **Security model** | Bolt-on, if any | Capability tokens, secret proxying, leak scanning, sandboxed tools |
| **Secret handling** | Pass API keys directly to the model | Secrets never reach the model — proxied, redacted, and scanned for 15+ leak patterns |
| **Tool access** | Allow-all or manual prompt engineering | Sandboxed filesystem, shell metacharacter blocking, SSRF protection, allow/deny lists |
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
weave workspace up demo
```

That is it. Everything runs locally — no external services required.

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

- [Manifest Reference](docs/manifest-reference.md) — full schema for workspace JSONC files
- [Tools](docs/tools.md) — connector interfaces, security features, and testing patterns
- [Architecture docs](docs/) — subsystem documentation for Foundation, Workspaces, Assistants, Tools, Security, Deployment, and Runtime

## Built With

- [.NET 10](https://dotnet.microsoft.com/) and [Orleans](https://learn.microsoft.com/dotnet/orleans/) for the actor-based runtime
- [Microsoft.Extensions.AI](https://devblogs.microsoft.com/dotnet/introducing-microsoft-extensions-ai-preview/) for model-agnostic AI integration
- [Aspire](https://learn.microsoft.com/dotnet/aspire/) for local orchestration and observability
- [Spectre.Console](https://spectreconsole.net/) for the interactive CLI

## License

Licensed under either of [Apache License, Version 2.0](LICENSE-APACHE) or [MIT License](LICENSE-MIT), at your option.
