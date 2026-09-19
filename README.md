# Weave

[![CI](https://github.com/KoalaFacts/Weave/actions/workflows/ci.yml/badge.svg)](https://github.com/KoalaFacts/Weave/actions/workflows/ci.yml)
[![Security Scan](https://github.com/KoalaFacts/Weave/actions/workflows/scan-security.yml/badge.svg)](https://github.com/KoalaFacts/Weave/actions/workflows/scan-security.yml)

> **An Agent Control Plane for the real world.**
>
> **Connect your agents. Control what they can do. See what they did.**

Weave governs how AI agents use tools, credentials, accounts, infrastructure, and other real-world resources.

It is **not another agent framework** and does not require an agent to adopt a Weave reasoning loop. An agent can come from OpenAI, Anthropic, an internal system, a CLI process, an MCP client, or the optional Weave Agent Runtime. Weave's enduring responsibility is the control plane around that agent:

- establish identity and execution context;
- decide what operations are allowed;
- bind the exact resource, account, installation, and credential;
- obtain approval when policy requires it;
- execute through a controlled adapter;
- persist accountable evidence of what happened.

The goal is simple: an agent should never gain authority merely because it can discover a tool or reach an API.

---

## Why Weave

As agents move from answering questions to taking actions, the difficult problems stop being model prompts.

They become control-plane problems:

| Question | Weave's direction |
|---|---|
| Which agent is acting? | Stable agent and subject identity |
| In what context is it acting? | Tenant, Workspace, and Room context |
| What is it allowed to do? | Explicit Authority and operation-level grants |
| Which account or asset may it use? | Resource bindings and constrained credentials |
| Does a human need to approve this exact action? | Durable approval bound to an immutable invocation plan |
| Which integration should execute it? | Explicit Plugin Installation and adapter routing |
| What if the request times out after dispatch? | Durable Attempts and explicit unknown outcomes |
| Can we prove what happened later? | Local invocation evidence and authorized audit |

Today these concerns are often spread across prompts, API keys, plugin configuration, custom middleware, and application-specific glue. Weave is intended to make them one coherent product boundary.

---

## The Control Plane

The target model separates **identity**, **context**, and **authority**.

```text
                       ┌──────────────────────┐
                       │   Agent / Human /    │
                       │  Service / Plugin    │
                       └──────────┬───────────┘
                                  │
                                  ▼
                       ┌──────────────────────┐
                       │ Trusted ingress and  │
                       │ subject/Tenant ctx   │
                       └──────────┬───────────┘
                                  │
                                  ▼
┌──────────────┐       ┌──────────────────────┐
│ Workspace /  │──────▶│      Authority       │
│ Room context │       │ grants + policy      │
└──────────────┘       └──────────┬───────────┘
                                  │
                                  ▼
                       ┌──────────────────────┐
                       │ Governed Invocation  │
                       │ exact frozen plan    │
                       └──────┬────────┬──────┘
                              │        │
                    approval  │        │ durable evidence
                              ▼        ▼
                       ┌──────────┐  ┌──────────┐
                       │ Execute  │  │  Audit / │
                       │ Adapter  │  │ Journal  │
                       └────┬─────┘  └──────────┘
                            │
                            ▼
                 ┌───────────────────────┐
                 │ Resource / API / SaaS │
                 │ account / MCP / CLI   │
                 └───────────────────────┘
```

An **Agent** is not a Workspace.  
A **Room** is not permission.  
A **Plugin** being installed is not permission.  
A discovered operation is not permission.  
Possessing a credential is not permission.

Those distinctions are deliberate.

---

## One Governed Path

The core product direction is a single governed invocation path:

```text
authenticated context
  -> resolve exact operation, resource, installation, and credential constraints
  -> normalize and freeze the operation plan
  -> evaluate current authority and policy
  -> obtain durable, specific approval when required
  -> revalidate authority, target, and plan
  -> persist the execution attempt and required local evidence
  -> execute through the selected adapter
  -> persist confirmed or unknown outcome
```

This path should be the same whether an operation starts from a Weave-hosted agent, an external agent, HTTP, MCP, CLI, or another supported ingress.

That is the boundary Weave is building around.

---

## Use the Ecosystem You Already Have

Weave should not require the world to rewrite integrations for Weave.

Existing systems can be adapted into governed operations:

| Interface / adapter | Role |
|---|---|
| **MCP** | Discover and invoke operations exposed by MCP servers |
| **CLI** | Execute explicitly mapped commands and arguments |
| **OpenAPI** | Adapt described HTTP operations |
| **Direct HTTP** | Call a constrained configured HTTP endpoint |
| **Dapr** | Invoke services through a Dapr sidecar |
| **Native .NET** | Implement trusted in-process capabilities where appropriate |

MCP, CLI, HTTP, gRPC, and similar protocols are **interfaces**, not separate security models.

Likewise, process, container, WASM, and in-process execution are **runtime/hosting choices**, not the primary plugin taxonomy.

### Plugin ownership

A plugin installation has an ownership model:

| Ownership | Meaning |
|---|---|
| **Internal** | Weave manages the supported lifecycle through an implementation it controls |
| **External** | Another system owns the execution lifetime; Weave connects, authenticates, discovers, and invokes |

Ownership is independent of language, protocol, process placement, or trust level.

A future ecosystem can therefore include C#, Rust, Python, containers, remote services, MCP servers, and other implementations without turning each transport into a new architectural category.

---

## Resources Are First-Class

Agents increasingly need more than abstract "tools". They need access to concrete things:

- a mailbox;
- a phone number;
- a browser profile;
- a GitHub account or repository;
- a cloud subscription;
- an accounting tenant;
- a CRM account;
- a database;
- a device or other managed asset.

Weave's target Resource model separates:

```text
Resource          what exists
ResourceBinding   where it is available
Authority         what may be done with it
Credential        how an approved adapter authenticates
PluginInstallation which provider/implementation handles it
```

Importing or binding a resource does **not** automatically grant operations on it.

Provisioning and reconciliation come after the simpler governed-operation path is proven. Weave does not need to become an email provider, telecom carrier, browser vendor, or cloud platform in order to govern access to those services.

---

## Current Status

> **Weave is pre-1.0 and under active architectural development.**
>
> The repository already contains substantial working software, but the current runtime and the target Agent Control Plane are not the same thing yet.

### Implemented today

The current repository includes:

- a flat **Vertical Slice + Composition** source foundation;
- a C#/.NET product project with feature-owned behavior;
- executable hosts separated from the product source;
- opt-in extensions for MCP, CLI tools, OpenAPI, Direct HTTP, filesystem, Dapr, and the existing Agent Runtime;
- capability-token validation and hardened revocation handling;
- fail-closed API authentication behavior;
- credential and secret-protection foundations;
- existing workspace, agent, tool, persistence, CLI, and runtime behavior retained through the structural migration;
- strict dependency auditing, build, test, and release-chain security gates.

### Not yet claimed as complete

The target architecture deliberately goes further than the current runtime.

In particular, the current implementation still retains:

- Agent actor keys based on `{workspaceId}/{agentName}`;
- tool-level authorization in places where the target requires exact operation-level authority.

The following are architectural targets being implemented as explicit vertical slices, not claims about completed functionality:

- stable portable Agent identity independent of a Workspace;
- Tenant and Room-scoped context;
- exact operation-level grants and delegation;
- generalized Resource bindings;
- durable Invocation and Attempt state;
- durable human approval bound to a frozen plan;
- generalized reconciliation of long-lived external resources;
- a language-neutral plugin ecosystem around the governed operation model.

See [ARCHITECTURE.md](ARCHITECTURE.md) for the authoritative target and the implementation notes under [docs/implementation](docs/implementation/) for incremental delivery records.

---

## Initial Product Profile: Governed Tools

The first product profile is intentionally smaller than a universal agent platform.

**Govern existing agents' access to existing tools.**

A useful first path is:

```text
existing agent
   |
   v
Weave Authority
   |
   v
Governed Invocation
   |
   +--> existing MCP server
   +--> existing CLI
   +--> existing HTTP/OpenAPI service
   +--> trusted native capability
   |
   v
durable result + audit evidence
```

Reasoning, memory, skills, scheduling, channels, marketplaces, model routing, and resource provisioning can consume this control plane, but they are not prerequisites for proving it.

---

## Architecture

Weave uses **Vertical Slice Architecture with explicit composition**.

Features own behavior.  
Slices complete use cases.  
Components provide replaceable behavior.  
Hosts compose the product.  
Extensions add independently selected implementations.

There is no compulsory `Core -> Application -> Infrastructure` hierarchy.

```text
src/
  Weave.csproj
  Agents/
  Workspaces/
  Authority/
  Invocations/
  Plugins/
  Resources/
  Credentials/
  Audit/
  Composition/
  ...real feature slices...

hosts/
  Weave.Host/
  Weave.Cli/
  Weave.Dashboard/
  Weave.AppHost/
  Weave.ServiceDefaults/

extensions/
  Weave.AgentRuntime/
  Weave.Mcp/
  Weave.CliTools/
  Weave.OpenApi/
  Weave.DirectHttp/
  Weave.FileSystem/
  Weave.Dapr/
  ...opt-in implementations...

tools/
  Weave.SourceGen/

tests/
  ...feature and compatibility test suites...
```

Feature folders are direct children of `src/`. Contracts stay with the feature that owns their meaning. Separate projects exist for real dependency, packaging, deployment, or isolation boundaries—not because every architectural noun needs an assembly.

Orleans remains useful where it solves a real runtime problem, but **Orleans is not the product architecture** and does not belong in external contracts.

For the full design, read [ARCHITECTURE.md](ARCHITECTURE.md).

---

## Security Model

Weave is a governance boundary only where it actually controls the path.

Important principles:

- **Discovery is not authorization.**
- **Installation is not permission.**
- **Client-supplied identifiers are requests to validate, not proof of authority.**
- **Unknown or incomplete security decisions fail closed.**
- **Approval applies to an exact plan, not a vague tool name.**
- **Credentials are referenced and constrained; they are not ordinary model-visible strings.**
- **Mandatory execution evidence must be persisted before privileged dispatch.**
- **Timeout after dispatch may mean `OutcomeUnknown`; it does not prove that no side effect occurred.**
- **In-process .NET code is trusted code.** An interface or `AssemblyLoadContext` is not a security sandbox.
- **Out-of-process does not automatically mean isolated.** Filesystem, network, process, and credential access must actually be constrained.
- **Redaction is defense in depth**, not a guarantee that prompt injection or exfiltration is impossible.

An agent that already holds independent upstream credentials and unrestricted network access can bypass Weave. Deployment boundaries must therefore be designed to make Weave the actual controlled route when governance matters.

---

## Build From Source

Weave currently targets the .NET SDK pinned in [global.json](global.json).

```bash
git clone https://github.com/KoalaFacts/Weave.git
cd Weave

dotnet restore Weave.slnx
dotnet build Weave.slnx --no-restore -c Release
dotnet test --solution Weave.slnx --no-build -c Release

# Explore the current CLI
dotnet run --project hosts/Weave.Cli -- --help
```

The existing CLI and runtime predate parts of the new control-plane model, so command surfaces will continue to evolve during the pre-1.0 rewrite.

### Current install scripts

Existing packaged CLI releases can also be installed with:

| Platform | Command |
|---|---|
| **Windows** | `irm https://raw.githubusercontent.com/KoalaFacts/Weave/main/scripts/install.ps1 \| iex` |
| **macOS / Linux** | `curl -fsSL https://raw.githubusercontent.com/KoalaFacts/Weave/main/scripts/install.sh \| sh` |
| **.NET tool** | `dotnet tool install --global Weave.Cli` |

---

## Development Principles

When contributing to Weave:

1. **Complete use cases vertically.** Do not recreate universal Core/Application/Infrastructure layers.
2. **Keep ownership explicit.** A feature owns its state and semantics.
3. **Compose rather than inherit.** Introduce interfaces/components at demonstrated variability boundaries.
4. **Keep authority explicit.** Never create an alternate ungoverned execution path.
5. **Do not confuse protocol with trust.** MCP, CLI, HTTP, process, container, and WASM do not determine permission by themselves.
6. **Tell the truth about guarantees.** A green build is not a sandbox; a local transaction is not exactly-once execution of an external API.
7. **Test behavior and failure modes.** Security, recovery, concurrency, and compatibility claims need evidence.

Before changing product behavior, read:

- [ARCHITECTURE.md](ARCHITECTURE.md)
- [AGENTS.md](AGENTS.md)
- [CLAUDE.md](CLAUDE.md)
- [docs/best-practices.md](docs/best-practices.md)

---

## Direction

The immediate sequence is intentionally pragmatic:

1. prove one deterministic governed operation end to end;
2. bring existing MCP/CLI/HTTP operations through that same governed path;
3. separate stable Agent identity, context, and authority;
4. introduce durable Invocation, Attempt, approval, and audit semantics;
5. bind real Resources and constrained credentials;
6. add provisioning/reconciliation only where real customer workflows require it;
7. grow a plugin ecosystem around actual demand rather than a closed catalogue.

A public marketplace, universal workflow engine, hosted model runtime, broad provider catalogue, email/SMS infrastructure, or mandatory microservice topology is **not** required before Weave can be useful.

---

## Documentation

- [ARCHITECTURE.md](ARCHITECTURE.md) — authoritative target architecture and security semantics
- [AGENTS.md](AGENTS.md) — coding-agent instructions and current implementation constraints
- [CLAUDE.md](CLAUDE.md) — repository development guidance
- [docs/best-practices.md](docs/best-practices.md) — security and engineering practices
- [docs/implementation/](docs/implementation/) — incremental implementation and verification records

---

## License

Dual-licensed under your choice of [MIT License](LICENSE-MIT) or [GNU Affero General Public License v3.0](LICENSE-AGPL).

SPDX: `MIT OR AGPL-3.0-or-later`.
