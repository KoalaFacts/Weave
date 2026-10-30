# Runtime-neutral contract

> **Status:** Target design. Nothing in this document is delivered yet except the existing [mailbox v1 protocol](../protocol/weave-mailbox/v1/README.md). Current behavior is defined by code, tests and the [implementation records](implementation/).

## Decision

Weave is an abstraction, not a particular codebase. Its normative definition is a **language-neutral contract**: wire protocols, state and outcome semantics, and executable conformance fixtures. Implementations conform to that contract; none of them is the definition.

- The current implementation is C#/.NET with Orleans as the actor runtime.
- A planned implementation targets TypeScript on Cloudflare Workers with Durable Objects as the actor runtime.
- A behavior is part of the contract only when it is written down here or under `protocol/` **and** has a conformance fixture. Behavior that exists only in one implementation is an implementation detail, however long it has existed.

The contract therefore does not need an implementation language. Language matters only for each implementation and its conformance runner.

## What the contract contains

| Layer | Expressed as | Example |
| --- | --- | --- |
| Wire protocol | Markdown semantics plus JSON Schema (draft 2020-12) under `protocol/<name>/v<N>/` | [`weave-mailbox/v1`](../protocol/weave-mailbox/v1/README.md) |
| Actor semantics | This document (see [Actor boundary](#actor-boundary)) | Single activation per key, serial handling, atomic state |
| Governance semantics | [ARCHITECTURE.md](../ARCHITECTURE.md) sections 5–8 and 13, plus the implementation records they cite | Journal before dispatch, unknown outcome, approval consumption atomic with attempt admission |
| Conformance fixtures | JSON cases under `protocol/<name>/v<N>/conformance/` | Given prior state and a request, the expected response and resulting state |

JSON Schema stays the schema language because the mailbox protocol already uses it and every target language can validate it. WIT/WebAssembly components are not the contract format: the 2026-10-30 evaluation compiled the .NET product to a WASI Preview 2 component and ran it in local `workerd`. It worked only after a workload fix and a `jco` transpile step, uploaded about 5.7 MB gzip and cost roughly 180–320 ms CPU per warm request. That makes it a possible deployment experiment, not the way to share semantics across implementations.

## Conformance fixtures

A fixture is a data file, not code, so every implementation runs the same cases:

- `given`: the prior state, expressed through contract operations or a declared seed format
- `when`: one or more requests in order, with the authenticated principal for each
- `then`: the expected responses (status, body subset or schema reference) and observable resulting state

Each implementation owns a small runner (xunit.v3 for .NET, `node:test` for TypeScript) that loads the fixtures, drives the implementation through its real public boundary (HTTP or the actor API, never private handlers) and reports every case. A runner that skips a fixture must say so; a skipped fixture is not conformance.

Fixtures must include denial and failure paths, not only the positive path: invalid or revoked credentials, unknown/ambiguous authority, duplicate submissions, expired items and unknown outcomes. Persistence, concurrency and isolation claims still need real-boundary tests in each implementation; a fixture that passes against an in-memory adapter does not prove them.

## Actor boundary

Implementations host stateful features behind a thin actor abstraction so that business code does not depend on Orleans or Durable Objects directly. The abstraction is an implementation pattern, not part of the wire contract, but every adapter must provide the same guarantees:

| Guarantee | Requirement | Orleans | Durable Objects | In-memory (tests) |
| --- | --- | --- | --- | --- |
| Addressing | An actor is identified by a string key; the key alone selects it | Grain key | `idFromName(key)` | Map key |
| Single activation | At most one live instance per key processes messages | Grain activation | One object per ID | One instance per key |
| Serial handling | Messages for one key are handled one at a time | Non-reentrant grain | Input/output gates | Per-key queue |
| Atomic state | A handler's state changes commit together or not at all | Explicit state write per turn | Storage transaction | Copy-on-commit |
| Timers | Durable wake-ups survive restarts | Reminders | Alarms | Fake `TimeProvider`/clock |

Cross-actor operations are not transactional. Where an invariant spans two actors, the contract states which actor owns it and how duplicates, retries and unknown outcomes are handled. Current actor keys such as `{workspaceId}/{agentName}` remain implementation keys; changing them still needs the stored-state and wire compatibility evidence required by [AGENTS.md](../AGENTS.md).

## First increments

1. Extract mailbox v1 conformance fixtures from the behavior already tested in `tests/Weave.Mailboxes.Tests`, and run them against the .NET mailbox host.
2. Describe governed invocation admission (invocation/attempt journal, duplicate submission, unknown outcome, approval consumption) as a protocol with fixtures, run against the .NET host.
3. Only then build a TypeScript implementation of the same slice on Workers/Durable Objects and require it to pass the same fixtures.

Each step is its own tested change. Nothing here authorizes removing or freezing the .NET implementation.

## Open decisions

- Which implementation receives new features first once both exist.
- Whether the TypeScript implementation lives in this repository.
- How Durable Objects storage limits and the lack of cross-object transactions map onto the current single-host SQLite journal guarantees.
