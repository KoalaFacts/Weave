# Weave

[![CI](https://github.com/KoalaFacts/Weave/actions/workflows/ci.yml/badge.svg)](https://github.com/KoalaFacts/Weave/actions/workflows/ci.yml)

**Security-first AI agent orchestration for .NET.** Define assistants, lock down their tools with capability tokens, and keep secrets from leaking — all from a single manifest file. Runs locally with one command.

Most agent frameworks give your AI unrestricted access and hope for the best. Weave starts from the opposite assumption: assistants get *only* the capabilities you explicitly grant, secrets are proxied and scanned for leaks, and every tool call goes through a security boundary you control.

## How Weave Is Different

| | Other agent frameworks | Weave |
|---|---|---|
| **Security model** | Bolt-on, if any | Capability tokens, secret proxying, leak scanning built in |
| **Secret handling** | Pass API keys directly to the model | Secrets never reach the model — proxied and redacted by default |
| **Tool access** | Allow-all or manual prompt engineering | Explicit allow/deny lists with wildcard patterns |
| **Architecture** | Single-process, in-memory | Orleans grain-based — each agent, tool, and workspace is an independent, recoverable actor |
| **Configuration** | Code-heavy setup | One JSONC manifest defines everything |
| **Runtime** | Cloud-only or single-machine | Local-first, scales to Kubernetes when you need it |

## Why Weave

- **Get started in minutes** — install, pick a preset, and you have a working setup.
- **You stay in control** — decide exactly which tools your assistants can use and what they are allowed to do.
- **Your data stays yours** — secrets are proxied, redacted, and scanned for leaks. They never reach the model directly.
- **See what is happening** — a live dashboard shows what your assistants are working on at any time.
- **Reuse what you build** — save your setup once and reuse or share it without starting over.
- **Grow at your own pace** — everything works locally out of the box. Add containers, Vault, or Dapr when you need them.

## Quick Start

Weave runs on Windows, macOS, and Linux. One command to install, one command to start.

**Install** — pick the one that matches your system:

| Platform | Command |
|----------|--------|
| **Windows** | `irm https://raw.githubusercontent.com/KoalaFacts/Weave/main/scripts/install.ps1 \| iex` |
| **macOS / Linux** | `curl -fsSL https://raw.githubusercontent.com/KoalaFacts/Weave/main/scripts/install.sh \| sh` |
| **.NET** | `dotnet tool install --global Weave.Cli` |

Or download directly from [GitHub Releases](https://github.com/KoalaFacts/Weave/releases).

**Create and run your first workspace:**

```bash
weave workspace new demo
weave workspace up demo
```

That is it. Everything runs locally on your machine by default — no extra services, no complicated setup. When you are ready for more, you can add advanced capabilities at your own pace.

## Configuring a Workspace

The fastest way to configure a workspace is through the CLI. Answer a few prompts or pick a preset and you are done.

### Start from a preset

```bash
# See available presets
weave workspace presets

# Create a workspace from a preset
weave workspace new demo --preset coding-assistant
```

Built-in presets get you running in seconds:

| Preset | What you get |
|--------|-------------|
| **starter** | One assistant, no tools — the simplest possible workspace. |
| **coding-assistant** | An assistant with git and file tools, ready for code tasks. |
| **research** | An assistant with web and document tools for gathering information. |
| **multi-agent** | A supervisor and worker assistants for more complex workflows. |

### Build interactively

No flags to memorize. The CLI gives you a rich interactive terminal where you select options, pick from lists, and confirm as you go:

```bash
weave workspace new demo
```

```
? Choose a preset:
  ❯ starter
    coding-assistant
    research
    multi-agent
    custom (configure everything yourself)

? Select a model for your assistant:
  ❯ claude-sonnet-4-20250514
    gpt-4o
    custom...

? Which tools should the assistant have access to?
  ❯ ◉ git
    ◉ filesystem
    ◯ web
    ◯ custom...

? What should the assistant be allowed to do?
  ❯ ◉ use all available tools
    ◯ read-only access
    ◯ custom...

✔ Workspace "demo" created. Run `weave workspace up demo` to start.
```

You can also add or change things individually at any time:

```bash
weave workspace add agent demo --name reviewer
weave workspace add tool demo --name web
weave workspace add target demo --name production
weave workspace show demo
weave workspace validate demo
```

Every command updates the workspace configuration for you. No manual file editing required.

### What you can configure

| Section | What it controls |
|---------|------------------|
| **workspace** | Privacy, security boundaries, and how secrets are managed. |
| **agents** | Which AI model to use, what it can do, and any recurring tasks. |
| **tools** | Which tools (commands, services, APIs, filesystem) the assistant can call. |
| **hooks** | Actions that run automatically at key moments (e.g. on start, on finish). |
| **targets** | Where the workspace runs — your machine, a server, or a CI pipeline. |
| **plugins** | Optional integrations (Dapr, Vault, webhooks) activated by environment. |

For advanced scenarios you can also edit the manifest JSONC file by hand. See the [Manifest Reference](docs/manifest-reference.md) for the full schema.

### Built-in tool types

Every tool runs inside a security boundary — capability tokens gate access, and inputs/outputs are scanned for leaked secrets.

| Type | What it does |
|------|-------------|
| **filesystem** | Sandboxed file access — read, write, edit, grep, search. All paths locked to a root directory with symlink escape prevention. |
| **cli** | Shell commands with allow/deny lists and shell metacharacter blocking. |
| **mcp** | Model Context Protocol servers over stdin/stdout. |
| **openapi** | HTTP APIs described by an OpenAPI spec, with SSRF protection. |
| **direct_http** | Lightweight HTTP calls to a base URL. |
| **dapr** | Dapr service invocation through the sidecar. |

## CLI Commands

The CLI is designed for a rich terminal experience — clear output, interactive prompts, and no guesswork.

```text
weave workspace new <name>          Create a new workspace
weave workspace list                List your workspaces
weave workspace remove <name>       Remove a workspace

weave workspace up <name>           Start a workspace
weave workspace down <name>         Stop a workspace
weave workspace status <name>       See what is happening in a workspace

weave workspace add agent <name>    Add an assistant
weave workspace add tool <name>     Add a tool
weave workspace add target <name>   Add a place to run the workspace

weave workspace show <name>         Show the current configuration
weave workspace validate <name>     Check that everything is set up correctly
weave workspace publish <name>      Generate files for deploying elsewhere
weave workspace presets             Browse ready-made workspace templates
```

## Built With

- [.NET 10](https://dotnet.microsoft.com/) and [Orleans](https://learn.microsoft.com/dotnet/orleans/) for the actor-based runtime
- [Microsoft.Extensions.AI](https://devblogs.microsoft.com/dotnet/introducing-microsoft-extensions-ai-preview/) for model-agnostic AI integration
- [Aspire](https://learn.microsoft.com/dotnet/aspire/) for local orchestration and observability
- [Spectre.Console](https://spectreconsole.net/) for the interactive CLI

## License

Licensed under either of [Apache License, Version 2.0](LICENSE-APACHE) or [MIT License](LICENSE-MIT), at your option.
