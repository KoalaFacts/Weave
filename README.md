# Weave

[![CI](https://github.com/KoalaFacts/Weave/actions/workflows/ci.yml/badge.svg)](https://github.com/KoalaFacts/Weave/actions/workflows/ci.yml)
[![Security Scan](https://github.com/KoalaFacts/Weave/actions/workflows/scan-security.yml/badge.svg)](https://github.com/KoalaFacts/Weave/actions/workflows/scan-security.yml)

**Let independently running agents contact each other and exchange messages.** Weave provides contact, inbox and outbox primitives. Agents choose how to admit peers, interpret opaque payloads and carry out their own collaboration. They can use the relay or their own direct method.

Weave is open-source and under active, pre-1.0 development. The first agent-network increment is an opt-in HTTP/SQLite mailbox relay with public or unlisted contact cards, explicit recipient decisions, retryable delivery and recipient-controlled ACK. It does not host reasoning, certify identities or members, or orchestrate business tasks. The existing **Governed Tools** runtime remains a separate working capability for operation grants, approvals and recorded outcomes.

## Try two independent endpoints

From a checkout with .NET SDK 10 and Node 24.19+, build the mailbox host and run the [independent endpoint walkthrough](examples/agent-network/README.md):

```bash
dotnet restore Weave.slnx --locked-mode
dotnet build hosts/Weave.Mailbox.Host/Weave.Mailbox.Host.csproj --no-restore -c Release
node examples/agent-network/run-demo.ts
```

The coordinator starts a real relay and two separate endpoint processes with independent state and mailbox-control credentials. It demonstrates public pending/rejected/explicit autoaccept, independently selected private admission, plaintext and encrypted opaque transport, pull/SSE retry, local dedup, ACK/expiry/block, retained restart and direct endpoint delivery with no relay copy. It uses only synthetic data and built-in Node modules, with no npm install, hosted agent or model account.

Public exposure permits a contact attempt; it never means acceptance. Unlisted distribution and audience hints do not implement group membership checks. Endpoint encryption is optional and strongly recommended: the demo's AES fixture is not a production E2EE adapter. Plaintext can be read by relay infrastructure. ACK removes the active relay body; it is not human reading, business completion or erasure of endpoint copies, WAL history and backups. See the [v1 protocol](protocol/weave-mailbox/v1/README.md) and [delivered scope](docs/implementation/2026-10-07-agent-contact-mailbox-mvp.md).

## Governed Tools: control operation authority

Connecting an agent to a tool often gives it several powers at once. A file tool can read and write; an API may expose both harmless queries and consequential changes. When a request times out, the caller may not know whether a change happened. A tool connection alone cannot answer **which operation was allowed, what was approved, or whether the effect was already attempted**.

Weave makes those decisions at the operation boundary. The repository's working filesystem example shows the difference:

| Step | What happens |
| --- | --- |
| An agent reads `meeting.txt` | Weave checks the `read_file` grant before calling the file tool. |
| The agent submits a write to `summary.md` | If `write_file` requires approval, Weave stores the proposed path and content and returns a pending result. The file is not written. |
| A separate reviewer examines the proposal | The reviewer sees the exact target, inputs, requester and plan digest. Approving records a decision; it does not run the write. |
| The original agent resumes the same invocation | Weave rechecks current authority and the approved plan, records an attempt, then dispatches. Submitting that invocation ID again does not repeat the write. |

This is an implemented, [executable local walkthrough](examples/governed-tools/host-demo.ts), not a claim that every connector already supports the same approval policy. The walkthrough uses a scripted reviewer and a deterministic client in place of a person and a live model.

## Use your Codex with local documents

With a published CLI-plus-Host bundle and an installed, signed-in Codex CLI:

```text
weave local init --documents documents
weave local serve
```

Keep the Host terminal open. In another terminal, run `weave local codex` and give
Codex your document task. When it reports a Pending UUID, personally review it
with `weave local review --id ORIGINAL_UUID --continue`. After your approval, the
CLI launches Codex to query and resume that same UUID, then verifies the recorded
outcome. Approval itself does not execute the write. `weave local status --id
ORIGINAL_UUID` queries a retained result without writing.

The bundle includes the .NET runtime; this route needs no SDK, Python, Node or
wrapper scripts. It is a local same-user profile, not an OS sandbox. See the
[complete first-use guide](docs/local-codex.md) for prerequisites, private storage,
credential expiry, restart and local publishing.

## What works today

- **Agent contact and mailboxes.** A separately composed relay publishes independently public/unlisted, long/short-lived cards. Recipient endpoints decide admission; accepted peers send bounded opaque bytes, pull or subscribe to pending bodies and explicitly ACK. Sender-scoped immutable retries return body-free receipts. Known-pair blocking fences admission and delivery; unblock needs fresh contact. The [independent endpoint example](examples/agent-network/README.md) also demonstrates a separate direct method without a relay copy
- **Exact tool-operation grants.** Tool availability and connection permission do not grant execution. A grant such as `tool:files:invoke:read_file` does not authorize `write_file`. The host checks signed capability tokens, expiry and revocation before dispatch.
- **Durable file-write approval.** When configured for a supported filesystem operation, Weave retains the original proposal and waits for an independently authorized decision bound to its target and inputs. The original caller must resume with the same ID and current execution rights.
- **Recorded attempts and outcomes.** The host uses an on-disk SQLite journal. It commits invocation and attempt evidence before an external call, exposes owner-scoped outcome lookup, and prevents duplicate dispatch for the same logical request. A timeout after dispatch can still mean an unknown external outcome.
- **Existing tool connections.** The repository includes filesystem, MCP, CLI, OpenAPI, Direct HTTP and Dapr adapters. MCP supports stdio and HTTP/SSE paths. Adapter capabilities and security boundaries differ; connecting a tool never grants its operations automatically.
- **Workspace and installation management.** A CLI and Orleans host manage manifest-based workspaces. Workspace-scoped Dapr and HTTP MCP installations retain configured identity and enabled state with durable actor storage. Installation observations describe registration; workspace recovery has a separate reconciliation status. Neither status proves an external connector is reachable.
- **Runtime observation and explicit recovery.** Authorized HTTP operations inspect retained Docker/Podman container IDs and network attachment. [Workspace readiness](docs/implementation/2026-10-01-workspace-service-readiness.md) combines these resources with current service health: a disconnected MCP tool or changed pinned contract prevents `Ready`, even after an earlier successful recovery. Installed HTTP MCP tools expose [per-installation diagnostics](docs/implementation/2026-09-30-workspace-mcp-observation.md). Recovery starts the original stopped container only when its network dependency is confirmed. After a host restart, explicit reconciliation can confirm [retained resources](docs/implementation/2026-09-30-workspace-runtime-reconciliation.md) and [restore installed HTTP MCP tools](docs/implementation/2026-09-30-workspace-mcp-restoration.md) from frozen definitions and pinned contracts. Agent and other service restoration remain blocked. See the [network recovery path and limits](docs/implementation/2026-09-30-workspace-network-recovery.md).

The [implementation records](docs/implementation/) describe the delivered scope and migration limits. The [architecture document](ARCHITECTURE.md) describes the broader target; it is not a list of shipped features.

## See the decision flow in memory

From a checkout, run this with Node.js 22.18.0+; it uses only built-in modules:

```bash
node examples/governed-tools/demo.ts
```

The sketch keeps the document, grants, pending write, approval and attempt count in memory. It shows an allowed read, a denied write, an approved request with no immediate effect, a resume that writes, and a repeated ID that does not write again. It needs no .NET build, server, database, model account, network connection or configuration, and creates no files. **This is a control-flow illustration, not the Weave Host or a security verification.**

For the actual Host path with signed authority and a durable journal, use the [Node governed-tools walkthrough](examples/governed-tools/README.md). It requires a .NET build and local storage, but no npm install or Python. The [trusted onboarding guide](docs/implementation/2026-09-22-trusted-operator-onboarding.md) covers operator setup.

## Current boundaries

- **Network scope is small.** One relay host and preserved SQLite file; no mandatory A2A server, member/identity certification, business scheduler, production E2EE adapter, NAT traversal, multi-node guarantee or hosted-agent wake. Explicit mailbox-control provisioning and production HTTPS are operator responsibilities. Relay delivery is at-least-once until ACK/expiry, with no exactly-once business-effect promise
- **Approval coverage is specific.** Filesystem is the first adapter with exact target binding for required approval. Reviewed HTTP decisions are opt-in. The Dashboard has a read-only review screen; it does not offer browser approval or human login.
- **Recovery is bounded.** Duplicate protection depends on the preserved single-host journal file and the same invocation ID. Outcome queries return metadata, not the original response body. An unknown outcome must be investigated; starting a new ID can repeat an external effect.
- **Management writes have a narrower journal.** Workspace start/stop and plugin connect/disconnect now record admission before effects. Retain `X-Weave-Management-Id` to inspect an uncertain result; the [management journal guide](docs/implementation/2026-09-29-management-operation-journal.md) lists the covered routes and limits.
- **Workspace restart needs inspection.** A persisted `Running` workspace can be marked as requiring reconciliation after a Host restart. That status does not mean its containers, agents or external plugins were automatically restored.
- **Deployment security is operator-owned.** API authentication defaults to `none` for local development. The built-in bearer mode is not a full OAuth/OIDC or tenant identity system. In-process plugins are trusted code, not sandboxed code. Protect the journal, credentials and host before exposing them beyond a private environment.

Portable Agent identity, Tenant/Room authority, generalized resource and credential constraints, and broader approval coverage remain design work. See [ARCHITECTURE.md](ARCHITECTURE.md) for those goals without assuming they already exist.

## Further reading

| If you need to… | Start here |
| --- | --- |
| Integrate contact/inbox/outbox or run independent endpoints | [Mailbox protocol](protocol/weave-mailbox/v1/README.md), [endpoint walkthrough](examples/agent-network/README.md) |
| Understand exact grants and existing-workspace migration | [Operation authority](docs/implementation/2026-09-19-exact-tool-operations.md) |
| Integrate durable IDs, outcome lookup or approval | [Invocation journal](docs/implementation/2026-09-19-durable-invocations.md), [approval guide](docs/implementation/2026-09-20-durable-approval.md) |
| Connect an existing agent over HTTP | [Governed HTTP entry](docs/implementation/2026-09-20-governed-http-entry.md), [proposal-by-UUID contract](docs/implementation/2026-09-24-authorized-proposal-uuid.md) |
| Build or install a tool plugin | [Plugin examples](examples/plugins/echo/README.md), [MCP installation guide](docs/implementation/2026-09-26-mcp-echo-installation.md) |
| Inspect command capacity or unconfirmed process cleanup | [Process runtime diagnostics](docs/implementation/2026-10-03-process-runtime-observation.md) |
| Review product direction and contribution rules | [Architecture](ARCHITECTURE.md), [contributor instructions](AGENTS.md) |

Weave is available under [MIT](LICENSE-MIT) **or** [AGPL-3.0-or-later](LICENSE-AGPL).
