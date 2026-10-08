# Agent contact and mailbox MVP

2026-10-07

## Delivered scope

Weave now has a separate opt-in contact/inbox/outbox relay for independently running agents. The product contracts live in `src/Contacts` and `src/Mailboxes`; `extensions/Weave.Mailboxes.Sqlite` owns the SQLite provider; `hosts/Weave.Mailbox.Host` composes HTTP, explicit mailbox-control authentication, bounded SSE and cleanup. The mailbox host has no AgentRuntime or Orleans dependency. It does not run reasoning, certify identity or membership, grant tool authority or orchestrate business work.

The existing Governed Tools host, capability tokens, exact operation grants, durable approval/invocation journals and workspace/runtime behavior remain separate capabilities. This increment deliberately changes the primary product positioning, not those security or runtime contracts.

## Contact and control

- One mailbox may publish many independently public/unlisted and long/short-lived cards with method instructions and optional audience hints. Public discovery reveals only eligible public cards. An unlisted card can be owner-exported and shared out of band; its ID permits an attempt without exposing its details through discovery
- Visibility never chooses acceptance. Requests enter pending. Only the actual recipient records pending, needsAction, accepted or rejected, optionally replying through the requester's expiring inbox. Both participants query body-free metadata. Member/peer authentication and any autoaccept policy belong to the endpoint
- Mailbox control is derived from a secret header matched against trusted configured hashes. There is no default secret, public credential issuer/signup or member identity system. Local loopback HTTP is for tests/development; operators must separately provision protected configuration and production HTTPS
- Contact request identity is `(requesterMailboxId, requestId)`. The requester is derived from control on admission. A participant supplies the full locator on reads/decisions, but that locator grants no authority. Mailbox and card identifiers are JSON or once-percent-encoded query values; only canonical lowercase UUIDs occupy route segments
- Known-pair blocking fences matching new requests and delivery through all cards. Each participant controls only its own flag. Block/unblock advances channel generation; unblocking restores neither old bodies nor the contact. Fresh admission requires a new request and recipient decision

## Delivery and deletion

The relay stores one bounded pending body and exposes recipient-owned inbox bodies plus sender-owned body-free receipts. A UUIDv7 is bound to its immutable creation time. First admission permits five minutes age and 30 seconds future skew; payloads are at most 64 KiB, with positive lifetime at most 24 hours. Identical sender-scoped retries return the same receipt; changed input conflicts. Terminal body-free receipts last seven days. Purging a receipt cannot refresh the creation time or resurrect its old ID.

Pull, SSE, cursors and Last-Event-ID never ACK. Delivery is at-least-once until recipient-controlled ACK, expiry or block; clients need endpoint-local receipt/dedup checkpoints. ACK means relay acceptance/deletion, not human reading or task completion. Active reads enforce expiry/block/generation before cleanup; ACK/terminalization clears body and size atomically. This is single-host, retained-file durability, not multi-node or exactly-once business-effect execution.

Endpoints may encrypt before admission, which is strongly recommended. The relay transports opaque bytes and receives no endpoint decryption key. Plaintext necessarily remains visible to relay memory/storage and privileged infrastructure. Routing, IDs, size, timestamps and status remain visible even with encryption. Logical body deletion does not erase historical WAL/media, backups, responses already in flight or endpoint copies. Production provider logging/backups/recovery policies were not deployed or verified.

## Retained-state compatibility

Opening a retained schema-1 database transactionally migrates it to schema 2, scoping request keys and channel/message-purpose references by requester. The migration preserves row sequences/high-water marks, fingerprints, payload bytes, channel generations/blocks and already-null terminal bodies; failure rolls back and startup fails closed. It never resets storage or reconstructs a deleted payload. The host requires existing storage by default; `Mailbox__AllowCreateStorage=true` explicitly permits a new file.

Restarting on the same preserved file retains current metadata and terminal deletion state. Restoring historical backups with stale deletion/revocation records is outside the validated recovery path and must not be represented as safe recovery.

## Independent endpoint evidence

The [runnable walkthrough](../../examples/agent-network/README.md) resolves a supplied state path once against its original working directory, preserving existing supplied content by refusing a nonempty root. It starts a real relay process and two separate Node endpoints, with distinct control scopes, local files, contact policies and HTTP listeners. It uses only synthetic data and built-in modules. Acceptance tests establish signal-zero process liveness with live/stopped and deliberate-live negative controls, exercise noncooperative cancellation before readiness and during a pending command, and inspect live HTTP/SSE responses, active SQLite bytes, endpoint-local receipts across restart, terminal payload removal and a direct-message ID absent from relay rows/outbox. Readiness/command waits observe cancellation so cleanup is always reached; termination escalates from SIGTERM to SIGKILL after five seconds, with separately bounded close/drain observation and no success report for unconfirmed cleanup. The underlying suites cover exact expiry/ID bounds, idempotence/conflict, quotas, races, migration, identifier aliases, requester collisions, lost ACK and multi-cycle stream behavior.

The example's optional AES-256-GCM uses a temporary synthetic symmetric key passed only to the endpoint processes. It demonstrates opaque-byte transport and endpoint decryption; it is not a production client E2EE adapter, key-distribution/identity scheme or OpenPGP interoperability. Direct HTTP uses an independently admitted peer and separate test control; its receipts, expiry and copies belong to the endpoints. No NAT traversal, hosted-agent wake, A2A task server, business execution, production deployment, paid provisioning or real credentials are included. These same-user test processes are not an OS sandbox.

## Verification and remaining gates

Release build, mailbox acceptance/storage/HTTP/SSE regressions, repository Python checks, formatting and dependency results are recorded against the delivered source. Standard `dotnet test` and solution-aware semantic formatting need AF_UNIX build/test pipes, which the execution sandbox denies; supported direct xUnit assembly entrypoints and folder whitespace checks are distinct alternatives, not weakened test/security settings. Wider-suite environment failures and existing platform/opt-in skips are reported separately. Test counts do not establish coverage; coverage collection/ownership thresholds and remote CI require their own evidence.

No dependency package, warning/audit relaxation, new test skip, authentication bypass or existing governed-operation change is part of this increment. See the [protocol](../../protocol/weave-mailbox/v1/README.md) for exact route/schema definitions, precision requirements, quotas, SSE limits and operator startup configuration.
