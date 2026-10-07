# Agent Contact Mailbox MVP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a tested contact/inbox/outbox network path between two independently running agent endpoints, with recipient-owned admission, optional endpoint encryption, and bounded relay retention.

**Architecture:** Add owned Contacts and Mailboxes features to the existing product, an optional SQLite persistence extension, and a separately runnable minimal ASP.NET Core mailbox host. The existing governed-tool runtime remains intact. Contact methods can advertise relay or direct endpoint routes; the relay carries opaque handshake/message bodies and records decisions made by the recipient, without implementing its identity, member-authentication, or business-task policies.

**Tech Stack:** C#/.NET 10; repository-pinned Microsoft.Data.Sqlite; ASP.NET Core; xunit.v3, Shouldly, FakeTimeProvider; Node 24 process fixtures using built-in modules.

**Spec:** [Approved network specification](../specs/2026-10-07-agent-contact-mailbox-mvp.md), derived from the reviewed Library document plus the user's October 7 clarifications that public visibility does not imply acceptance, and agents can block peers.

## Global Constraints

- Core only: contact, inbox, outbox. No business orchestration, trust certification, mandatory peer/member authentication, or mandatory A2A server.
- One agent may create many cards. Each card independently chooses public/unlisted distribution, long/short lifetime, handshake methods and optional audience hints; recipient admission remains independent. Long-lived public and long-lived group/intranet-shared cards are both valid. Public discovery must not leak other cards.
- Public cards permit contact attempts, not automatic acceptance. Autoaccept requires an explicit endpoint policy; public pending and public rejected outcomes are required.
- Recipient agents control blocking. Proposed rule: blocking stops future matching requests/admission/delivery; unblock does not restore contact or old payloads.
- Long-term cards last until revoked; short-term cards have an expiry. Suggested default expiry affects new contact establishment only.
- Endpoint encryption is optional and strongly recommended; no client E2EE adapter in this increment. No relay-held decryption keys.
- Transport is at-least-once until ACK/expiry, with idempotent admission; no exactly-once business-effect claim.
- Suggested defaults: card TTL 24 hours; message TTL at most 24 hours; payload at most 64 KiB; terminal receipts 7 days; first-admission window 5 minutes plus 30 seconds future skew.
- IDs bind their creation timestamp; tombstone purge must not permit old-ID resurrection. Payload equality is checked via bounded immutable bytes/digest.
- Relay ACK is recipient-controlled acceptance/deletion, not human reading or task completion. Pull/SSE/cursors never ACK.
- Retain the exact source-layout, token, secret, existing governed-operation and warning/audit invariants in AGENTS.md.
- No production deployment, paid resources, production credentials, package releases, merges or changes to other repositories.

## Review Focus

1. A public agent whose policy rejects contact must not accidentally acquire a communication grant; cover in Task 1.
2. Delayed accept/unblock from an old contact generation must not overturn a current block; cover in Tasks 1 and 2.
3. Message expiry, block and ACK can race with stream delivery; evaluate delivery eligibility immediately before emission, bound buffers, and disclose that already-sent bytes cannot be recalled; cover in Tasks 2 and 3.
4. Sender-supplied labels and reply addresses are untrusted: mailbox-control authorization must be bound server-side, and the relay must never fetch payload URLs; cover in Task 3.
5. Recovery, receipt cleanup and process restarts must not resurrect deleted content or repeat local acceptance; cover in Tasks 2 and 4.

## File ownership and chosen initial scope

- `src/Contacts/`: card validation, supported contact-method descriptors and typed recipient decisions; no endpoint policy implementation.
- `src/Mailboxes/`: transport envelope/result contracts, limits, expiry/idempotency rules and the store interface.
- `extensions/Weave.Mailboxes.Sqlite/`: durable cards, requests, contact generations, block records and message lifecycle in one transactional store. Concrete SQLite dependencies stay outside product.
- `hosts/Weave.Mailbox.Host/`: HTTP ingress, mailbox-control authentication, route mapping, bounded SSE and cleanup composition. It does not load the Orleans reasoning host.
- `tests/Weave.Mailboxes.Tests/`: pure rules, real SQLite restart/concurrency tests and real HTTP tests.
- `examples/agent-network/`: two independently launched deterministic agent endpoint fixtures and a coordinator. Their decisions are local fixture policies, never relay defaults.
- `protocol/weave-mailbox/v1/`: implemented envelope/contact schemas and canonical examples only.
- `docs/implementation/2026-10-07-agent-contact-mailbox-mvp.md`: exact delivered surface and verification limitations.
- `README.md`, `ARCHITECTURE.md`, `AGENTS.md`: brief current-versus-target positioning update; preserve existing behavior and security instructions.
- New project entries in `Weave.slnx`; generated lockfiles for affected projects; no unrelated dependency upgrades.

### Task 1: Contact cards and recipient decisions

**Files:** Create `src/Contacts/ContactCard.cs`, `ContactMethod.cs`, `ContactCardPolicy.cs`, `ContactRequest.cs`, `ContactRequestSummary.cs`, `ContactDecision.cs`, `ContactRelation.cs`, `ContactVisibility.cs`, `ContactStatus.cs`, and `tests/Weave.Mailboxes.Tests/Contacts/ContactPolicyTests.cs`. Fold the test project and solution entry into this task.

**Interfaces:**
- `ContactCard`: CardId, OwnerMailboxId, Visibility (Public/Unlisted), optional AudienceHint, CreatedAt, ExpiresAt (nullable for long-term), RevokedAt, and immutable Methods. One mailbox/agent can own multiple cards with unrelated exposure and expiry.
- Unlisted cards are owner-exported and shared out of band; an audience hint such as a group or intranet is descriptive, not an enforced member directory. Actual restricted access is enforced by the recipient method/network. No group membership system is added.
- `ContactMethod`: MethodId, Version, Transport ("relay" or "direct"), Endpoint, opaque bounded Instructions. No centrally interpreted credential schema.
- `ContactRequest`: RequestId, CardId, RequesterMailboxId, RecipientMailboxId, MethodId, Generation, CreatedAt, ExpiresAt, owned opaque Payload.
- `ContactDecision`: RequestId, ExpectedGeneration, Status (Pending/NeedsAction/Accepted/Rejected), optional ReplyEnvelope with its own frozen transport ID and expiry. Request Payload and decision Reply are transient inputs, never retained in contact rows.
- `ContactRequestSummary`: RequestId, CardId, participating mailbox IDs, MethodId, Generation, Status, CreatedAt, ExpiresAt, request/reply message references; no body. `ContactRelation` likewise retains state and references only.
- `ContactCardPolicy.Validate(card, now)` returns typed valid/invalid/expired/revoked result; `CanDiscover(card, callerMailboxId)` depends only on visibility/ownership.
- `ContactRelation.ApplyRecipientDecision(decision, now)` and `SetBlocked(blocked, expectedGeneration, now)` return typed outcomes; only host-authenticated recipient control invokes them.

- [ ] Write behavior tests before implementations. Required names include `Discover_PublicCard_DoesNotAcceptContact`, `Discover_AgentWithPublicAndUnlistedCards_DoesNotLeakUnlistedCard`, `Create_MultipleLongLivedCards_PreservesIndependentExposure`, `Decide_PublicRequest_PendingRemainsPending`, `Decide_PublicRequest_RejectDoesNotGrantDelivery`, `Decide_ExplicitEndpointAccept_RecordsAccepted`, `Validate_ShortCardAtExpiry_RejectsNewRequest`, `ExpireCard_ExistingContact_IsUnchanged`, `ChooseMethod_UnknownMethod_ReturnsUnsupported`, `Block_AcceptedContact_OverridesDelivery`, and `Decide_OldGenerationCannotOverrideBlock`.
- [ ] Run the focused contact tests; record the intended red result before supplying behavior. Add only the minimal compiling contract/test scaffolding required to run assertions.
- [ ] Implement immutable contracts and rules; public/private must never select a decision. Preserve separate long-term/short-term lifetime.
- [ ] Run focused tests and the project suite; verify private/public crossed with accept/pending/reject, plus unblock remaining disconnected.
- [ ] Apply repository check-rules/adversarial review and commit the independently testable contact path.

### Task 2: Durable inbox and outbox lifecycle

**Files:** Create branded mailbox/contact/message ID types in their respective owned feature folders, plus `src/Mailboxes/MailboxError.cs`, `MailboxPage.cs`, `ReceivedMailboxMessage.cs`, `src/Mailboxes/MailboxEnvelope.cs`, `MailboxReceipt.cs`, `MailboxOptions.cs`, `MailboxAuthority.cs`, `MailboxResult.cs`, `IExpiringMailboxStore.cs`; create `extensions/Weave.Mailboxes.Sqlite/Weave.Mailboxes.Sqlite.csproj`, `SqliteMailboxStore.cs` and responsibility-focused schema/contact/message/cleanup partial files; create `tests/Weave.Mailboxes.Tests/Storage/SqliteMailboxStoreTests.cs`, `MailboxRaceTests.cs`, and `MailboxRecoveryTests.cs`.

**Interfaces:**
- `MailboxAuthority` carries server-derived mailbox-control scope, never a caller-submitted peer identity.
- `MailboxEnvelope`: Version, MessageId, RecipientMailboxId, ContactGeneration, CreatedAt, ExpiresAt, PayloadEncoding, owned Payload. Sender scope comes from authority.
- `MailboxReceipt`: MessageId, SenderMailboxId, RecipientMailboxId, State, ExpiresAt, TerminalAt; never payload.
- `MailboxResult<T>` contains either Value or a typed `MailboxError` (Invalid/Unavailable/Forbidden/Conflict/Expired/Capacity). `MailboxPage<T>` contains immutable Items and nullable NextCursor. `ReceivedMailboxMessage` combines sender scope with an owned envelope. Keep each public type in its owning feature file.
- `IExpiringMailboxStore`: `PutCard(authority, card, ct)`, `FindCard(cardId, viewer, ct)`, `RequestContact(authority, request, ct)`, `ListContactRequests(authority, afterCursor, limit, ct)`, `GetContactRequest(authority, requestId, ct)`, `DecideContact(authority, decision, ct)`, `SetBlocked(authority, peerChannel, expectedGeneration, blocked, ct)`, `Send(authority, envelope, ct)`, `ReadPending(authority, afterCursor, limit, ct)`, `GetReceipt(authority, messageId, ct)`, `ListReceipts(authority, afterCursor, limit, ct)`, `Acknowledge(authority, senderMailboxId, messageId, ct)`, `Sweep(batchSize, ct)`. Every method accepts CancellationToken. `PutCard` returns `MailboxResult<ContactCard>`; `FindCard` returns `ContactCard?`; request/decision/block methods return `MailboxResult<ContactRelation>`; `ListContactRequests` returns `MailboxPage<ContactRequestSummary>` and `GetContactRequest` returns `ContactRequestSummary?`, restricted to recipient/requester participants; `Send` and `Acknowledge` return `MailboxResult<MailboxReceipt>`; `ReadPending` returns `MailboxPage<ReceivedMailboxMessage>`; `GetReceipt` returns `MailboxReceipt?`; `ListReceipts` returns `MailboxPage<MailboxReceipt>`; `Sweep` returns the count of terminalized/purged records. All returned data is owned.
- Contact request bodies and optional decision replies use the same expiring inbox transport and quotas as ordinary messages, with dedicated contact-purpose admission before acceptance. Request metadata and its envelope are created atomically; decision replies are bound to that exact request and cannot open an arbitrary-message bypass. Both sides discover state through bounded contact reads and retrieve bodies only from their own inbox. ACK/expiry/block removes handshake bodies using the same lifecycle; contact tables never archive them.
- Store uses injected TimeProvider; each mutation opens a short SQLite transaction and rechecks effective expiry, contact generation, block and quotas inside it. No transaction spans a decision or network stream.
- Configure bounded pilot admission (proposed): 100 mailboxes, 64 pending messages per mailbox, 32 MiB global pending payload, 8,000 total message rows, 32 pending contact requests per recipient, 1,000 active/retained request records and 5,000 contact/block records globally. Terminal request metadata is purged after 7 days; live relations remain bounded state without bodies. ACK and cleanup are not refused because new-message quota is full.

- [ ] Write real temporary-file SQLite tests for durable send/read, identical/conflicting duplicates, cross-mailbox isolation, no body in receipts, ACK ownership/idempotence, exact expiry, and public pending/rejected admission. Include participant-scoped pending-request/decision reads, needs-action replies, and restart recovery of those states.
- [ ] Run and retain the intended red evidence before implementation.
- [ ] Implement single-payload rows and metadata-only outbox; clear payload atomically on ACK; filter effective expiry/block on every read even before sweep; store minimal terminal receipts with 7-day purge. Reject stale timestamp-bound IDs after purge. Test handshake-body ACK/expiry/block deletion, pending-request quota pressure and absence of body columns in contact state.
- [ ] Add and run concurrency/restart tests: lost admission response; simultaneous duplicate sends; ACK-versus-expiry-versus-block; repeated quota release; delayed old-generation decisions; restart with same DB; isolated empty-state recovery that does not import old payloads or grants.
- [ ] Regenerate lockfiles using real restore, run locked restore and focused storage suite, then commit. Do not hand-edit lock hashes or substitute an in-memory production store.

### Task 3: Minimal relay HTTP host and bounded SSE

**Files:** Create `hosts/Weave.Mailbox.Host/Weave.Mailbox.Host.csproj`, `Program.cs`, `MailboxHostOptions.cs`, `MailboxControlAuthentication.cs`, `MailboxEndpoints.cs`, `ContactEndpoints.cs`, `MailboxJsonContext.cs`, `InboxEventStream.cs`, `MailboxCleanupService.cs`; create `tests/Weave.Mailboxes.Tests/Http/MailboxHttpTests.cs`, `ContactHttpTests.cs`, `InboxSseTests.cs`; add versioned schemas under `protocol/weave-mailbox/v1/`.

**Interfaces and route contracts:**
- Public card discovery returns only public, unexpired, unrevoked cards; private cards are exported by their owner and exchanged out of band.
- `GET /v1/contacts/requests` pages the caller's requests and `GET /v1/contacts/requests/{id}` returns participant-authorized metadata/state; unauthorized callers get no existence or body disclosure. Request/reply payloads are available only through the corresponding recipient inbox.
- `POST /v1/contacts/requests` records an opaque contact attempt; `POST /v1/contacts/requests/{id}/decision` records the exact recipient's decision. The relay never autoaccepts based on visibility.
- `PUT /v1/contacts/blocks/{channelId}` and `DELETE /v1/contacts/blocks/{channelId}` apply recipient blocking with expected generation.
- `POST /v1/messages`, `GET /v1/inbox`, `GET /v1/inbox/events`, `GET /v1/outbox`, `GET /v1/outbox/{messageId}`, `POST /v1/inbox/{sender}/{messageId}/ack` expose store contracts.
- First host uses explicitly configured mailbox-control credentials, hashes in service-side configuration and secret headers from endpoint hosts. No default credential, production registration/token issuer, agent identity provider or member-authentication endpoint. Synthetic tests use ephemeral credentials for test-only mailboxes.
- Owner card publication/listing/export and contact-request polling are authenticated; a private card's ID does not grant access to its details. Private invitation/handshake material remains endpoint-owned.
- JSON is bounded and source-generated; HTTP request cap 128 KiB, decoded payload cap 64 KiB, inbox page cap 10 messages. Errors distinguish invalid, unauthenticated, forbidden/unavailable, conflict, expired and capacity conditions without cross-mailbox existence leaks.
- SSE sends the same owned pending snapshots as pull, never auto-ACKs, flushes bounded events, stops on cancellation/control revocation and closes after 5 minutes. Suggested maximum 2 streams per mailbox, polling no faster than once per second in the pilot; reconnect is normal.

- [ ] Write real HTTP tests for owner-control isolation, invalid credentials failing closed, public discovery without autoaccept, pending/rejected/needs-action decisions, private discovery denial, bounds and no credential/payload logging. Verify recipient polling and requester pending/needs-action/accept/reject reads across a host/endpoint restart.
- [ ] Run the intended red tests, then compose the thin host and endpoints without importing the reasoning runtime or changing its authentication defaults.
- [ ] Write and run stream tests for replay before ACK, reconnect after lost ACK, slow readers, expiry/block immediately before each event, cancellation, and bounded stream slots/buffers.
- [ ] Add internal bounded cleanup, no public maintenance endpoint, no response-body cache; preserve ordinary ACK/cleanup capacity under admission pressure. Verify payload URLs are never fetched.
- [ ] Build and run all mailbox tests against actual HTTP and SQLite, then commit.

### Task 4: Independent endpoint demonstration and exact-head verification

**Files:** Create `examples/agent-network/agent.ts`, `run-demo.ts`, `README.md`; create `tests/Weave.Mailboxes.Tests/Acceptance/IndependentAgentsTests.cs`; update `README.md`, `ARCHITECTURE.md`, `AGENTS.md` positioning narrowly and add `docs/implementation/2026-10-07-agent-contact-mailbox-mvp.md`.

**Interfaces:**
- Coordinator launches the real mailbox host and two separate endpoint processes with independent local state and mailbox-control credentials.
- Each endpoint selects its own contact policy and consumes opaque payloads. Test scenarios cover public pending/reject/explicit autoaccept and private independently chosen policy.
- Direct endpoint method sends between the two endpoint HTTP listeners without relay messages. There is no NAT traversal product or claim.
- Test-only encryption fixture can encrypt before send and decrypt at the other endpoint with a reputable already-available primitive. It is evidence of opaque-byte transport only, not a shipped client E2EE adapter or OpenPGP interoperability claim.

- [ ] Write failing acceptance assertions for complete contact/send/pull/SSE/ACK flow between independent processes, endpoint-local dedup, expiry/block, restart and P2P no-relay-copy.
- [ ] Implement the smallest fixtures and run them with only synthetic data. Show encrypted and plaintext transport differences; inspect active SQLite/log output for the declared guarantees. Do not claim real hosted agents were awakened or that all provider backups were erased.
- [ ] Run `python3 -m unittest discover -s scripts/tests -v`, `dotnet restore Weave.slnx`, `dotnet restore Weave.slnx --locked-mode`, `dotnet build Weave.slnx --no-restore -c Release`, and `dotnet test --solution Weave.slnx --no-build -c Release`. Run repository formatting/dependency/security gates without weakening them.
- [ ] Obtain fresh independent whole-branch code review, resolve findings with regressions, rerun affected and aggregate gates on the final head.
- [ ] Push non-force, open/update a draft PR, confirm remote head and its CI, and report pass/fail/skipped/blocked checks separately. Do not merge or deploy.

## Review and execution choice

The specification is approved. This implementation plan is the next review artifact, not an assertion that code exists. Recommended execution is subagent-driven with one owner per task and independent whole-branch review; the scope stays one network vertical slice. Approving a different execution method does not change its acceptance criteria.

Environment: attached authorized desktop is currently offline; no saved cloud coding environment is available. An isolated cloud checkout is ready on `feat/agent-contact-mailbox-mvp`, based on `a7983cc73e05d9d0bb6afb147ab30396b74e6fde`. Node 24 and Python are present, but no .NET SDK is installed. Proposed environment step requiring approval: install Microsoft's official stable .NET SDK 10.0.401 into a task-local directory for build/test, without system security changes. Historical CI for the base commit passed; a fresh Weave Actions run has not yet been tested, and current runner/billing availability is unverified. Do not infer current availability from the old run.

Before production deployment, separately approve the chosen account/cost/access and verify provider logs, backups and recovery retention. This plan does not authorize those actions.
