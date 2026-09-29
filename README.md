# Weave

[![CI](https://github.com/KoalaFacts/Weave/actions/workflows/ci.yml/badge.svg)](https://github.com/KoalaFacts/Weave/actions/workflows/ci.yml)
[![Security Scan](https://github.com/KoalaFacts/Weave/actions/workflows/scan-security.yml/badge.svg)](https://github.com/KoalaFacts/Weave/actions/workflows/scan-security.yml)

**Control what AI agents can do with your tools.** Weave sits between an existing agent and the tools it calls. For supported operations, it checks a specific permission, can hold a request for independent approval, and records the attempt and outcome so a lost response does not invite a blind repeat.

Weave is an open-source Agent Control Plane under active, pre-1.0 development. Its first working profile is **Governed Tools**. You can use an existing agent; adopting Weave's own agent runtime is not required for the governed tool path.

## The problem Weave solves

Connecting an agent to a tool often gives it several powers at once. A file tool can read and write; an API may expose both harmless queries and consequential changes. When a request times out, the caller may not know whether a change happened. A tool connection alone cannot answer **which operation was allowed, what was approved, or whether the effect was already attempted**.

Weave makes those decisions at the operation boundary. The repository's working filesystem example shows the difference:

| Step | What happens |
| --- | --- |
| An agent reads `meeting.txt` | Weave checks the `read_file` grant before calling the file tool. |
| The agent submits a write to `summary.md` | If `write_file` requires approval, Weave stores the proposed path and content and returns a pending result. The file is not written. |
| A separate reviewer examines the proposal | The reviewer sees the exact target, inputs, requester and plan digest. Approving records a decision; it does not run the write. |
| The original agent resumes the same invocation | Weave rechecks current authority and the approved plan, records an attempt, then dispatches. Submitting that invocation ID again does not repeat the write. |

This is an implemented, [executable local walkthrough](examples/governed-tools/host-demo.ts), not a claim that every connector already supports the same approval policy. The walkthrough uses a scripted reviewer and a deterministic client in place of a person and a live model.

## What works today

- **Exact tool-operation grants.** Tool availability and connection permission do not grant execution. A grant such as `tool:files:invoke:read_file` does not authorize `write_file`. The host checks signed capability tokens, expiry and revocation before dispatch.
- **Durable file-write approval.** When configured for a supported filesystem operation, Weave retains the original proposal and waits for an independently authorized decision bound to its target and inputs. The original caller must resume with the same ID and current execution rights.
- **Recorded attempts and outcomes.** The host uses an on-disk SQLite journal. It commits invocation and attempt evidence before an external call, exposes owner-scoped outcome lookup, and prevents duplicate dispatch for the same logical request. A timeout after dispatch can still mean an unknown external outcome.
- **Existing tool connections.** The repository includes filesystem, MCP, CLI, OpenAPI, Direct HTTP and Dapr adapters. MCP supports stdio and HTTP/SSE paths. Adapter capabilities and security boundaries differ; connecting a tool never grants its operations automatically.
- **Workspace and installation management.** A CLI and Orleans host manage manifest-based workspaces. Workspace-scoped Dapr and HTTP MCP installations retain configured identity and enabled state with durable actor storage. Installation observations describe registration; workspace recovery has a separate reconciliation status. Neither status proves an external connector is reachable.

The [implementation records](docs/implementation/) describe the delivered scope and migration limits. The [architecture document](ARCHITECTURE.md) describes the broader target; it is not a list of shipped features.

## See the decision flow in memory

From a checkout, run this with Node.js 22.18.0+; it uses only built-in modules:

```bash
node examples/governed-tools/demo.ts
```

The sketch keeps the document, grants, pending write, approval and attempt count in memory. It shows an allowed read, a denied write, an approved request with no immediate effect, a resume that writes, and a repeated ID that does not write again. It needs no .NET build, server, database, model account, network connection or configuration, and creates no files. **This is a control-flow illustration, not the Weave Host or a security verification.**

For the actual Host path with signed authority and a durable journal, use the [Node governed-tools walkthrough](examples/governed-tools/README.md). It requires a .NET build and local storage, but no npm install or Python. The [trusted onboarding guide](docs/implementation/2026-09-22-trusted-operator-onboarding.md) covers operator setup.

## Current boundaries

- **Approval coverage is specific.** Filesystem is the first adapter with exact target binding for required approval. Reviewed HTTP decisions are opt-in. The Dashboard has a read-only review screen; it does not offer browser approval or human login.
- **Recovery is bounded.** Duplicate protection depends on the preserved single-host journal file and the same invocation ID. Outcome queries return metadata, not the original response body. An unknown outcome must be investigated; starting a new ID can repeat an external effect.
- **Workspace restart needs inspection.** A persisted `Running` workspace can be marked as requiring reconciliation after a Host restart. That status does not mean its containers, agents or external plugins were automatically restored.
- **Deployment security is operator-owned.** API authentication defaults to `none` for local development. The built-in bearer mode is not a full OAuth/OIDC or tenant identity system. In-process plugins are trusted code, not sandboxed code. Protect the journal, credentials and host before exposing them beyond a private environment.

Portable Agent identity, Tenant/Room authority, generalized resource and credential constraints, and broader approval coverage remain design work. See [ARCHITECTURE.md](ARCHITECTURE.md) for those goals without assuming they already exist.

## Further reading

| If you need to… | Start here |
| --- | --- |
| Understand exact grants and existing-workspace migration | [Operation authority](docs/implementation/2026-09-19-exact-tool-operations.md) |
| Integrate durable IDs, outcome lookup or approval | [Invocation journal](docs/implementation/2026-09-19-durable-invocations.md), [approval guide](docs/implementation/2026-09-20-durable-approval.md) |
| Connect an existing agent over HTTP | [Governed HTTP entry](docs/implementation/2026-09-20-governed-http-entry.md), [proposal-by-UUID contract](docs/implementation/2026-09-24-authorized-proposal-uuid.md) |
| Build or install a tool plugin | [Plugin examples](examples/plugins/echo/README.md), [MCP installation guide](docs/implementation/2026-09-26-mcp-echo-installation.md) |
| Review product direction and contribution rules | [Architecture](ARCHITECTURE.md), [contributor instructions](AGENTS.md) |

Weave is available under [MIT](LICENSE-MIT) **or** [AGPL-3.0-or-later](LICENSE-AGPL).
