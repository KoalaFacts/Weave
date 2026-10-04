# Encrypted agent mailbox on Cloudflare

**Status:** Written specification for review, 2026-10-04. The user approved the revised scope and asked for end-to-end progress. This document selects reviewable implementation details; no mailbox code, Cloudflare resources, credentials, payment, or deployment have been created. Written-spec and implementation-plan review remain separate from scope approval.

**Decision:** Weave provides a small authenticated, encrypted inbox/outbox API. Agents connect directly with ordinary HTTP: POST to send, pull or SSE to receive, and an explicit recipient ACK to burn the stored payload. Every message has an expiry. Selected A2A requests and responses travel inside the end-to-end encrypted envelope. No mandatory CLI, SDK, sidecar, hosted reasoning loop, or agent-specific runtime is required.

**Baseline:** Repository main `8e60a6440f2885add8a4ce2021683b47ddfde572`. This specification supersedes the implementation direction in [the earlier A2A design](2026-10-03-a2a-collaboration-foundation-design.md) and [its unexecuted plan](../plans/2026-10-04-a2a-collaboration-foundation.md). Existing governed-tool behavior and the landed Host boundary fixes are retained. This document does not assert that the earlier live-human or OS-isolation acceptance tests have run.

## 1. Intended outcome and limits

Two independently controlled agents can create distinct accounts, authenticate without handing private keys to Weave, mutually approve contact, and exchange encrypted A2A messages through a Cloudflare-hosted mailbox. Either endpoint can be behind home/mobile NAT and can be offline. One dot may control several independent accounts; account membership, contacts, quotas, keys, and inboxes do not automatically cross those identities. Optional proof of a human owner does not confer execution authority.

The first complete path uses two synthetic agent endpoints and one relay. It demonstrates registration, login, mutual friendship, encrypted request/reply, pull, SSE, reconnect, duplicate handling, receiver ACK, and expiry. The endpoint examples are conformance fixtures, not a mandatory client product. A real agent needs an authorized host able to perform HTTPS, use an existing OpenPGP implementation, protect keys, and retain its own acceptance/deduplication state.

Public-chain-verifiable identity addresses remain a product requirement. No chain has been chosen. The mailbox account fingerprint specified here is not a blockchain address or proof of an on-chain identity. Chain selection and a real signed address-binding adapter are a separate release prerequisite for claiming chain identity; the first local path reports `chainIdentity: unconfigured`. It performs no wallet funding, transactions, identity registration on a public chain, or owner-proof verification. Chat, memory, tasks, contact relationships, and private mailbox addresses never go on-chain.

Non-goals: a marketplace, libp2p, SimpleX integration, group chat, attachments, multi-device synchronization, account-key recovery/rotation, a task execution engine, automatic tool permission, and a full A2A server implementation. Receiving a friend's message does not authorize an agent to run tools or bypass its existing approvals.

## 2. Hosting choice and ownership

Use one TypeScript Worker and one D1 database for the initial deployment. Pull and bounded SSE use the same authoritative query. No Durable Object, Queue, R2 bucket, KV cache, or external database is necessary for this increment.

Workers has first-class JavaScript/TypeScript support; the existing ASP.NET Core/Orleans host is not directly deployable as a Worker. This is a separately deployable, opt-in mailbox module, not a rewrite or relocation of the entire C# application. Wasm or a containerized .NET host adds no value to this first path. [Cloudflare language support](https://developers.cloudflare.com/workers/languages/)

Follow the repository's feature ownership: account enrollment/session behavior, contacts, and message lifecycle stay in the opt-in `extensions/Weave.Mailbox.Cloudflare/` package; a thin `hosts/Weave.Mailbox.Worker/` entry point composes them. The deployment/runtime dependency justifies this package boundary. Do not introduce a universal Core, duplicate existing C# authority/journal semantics, or place business decisions in the Worker entry point. Versioned mailbox schemas and cross-language fixtures belong under `protocol/weave-mailbox/v1/`; tests belong under `tests/Weave.Mailbox.Cloudflare.Tests/`. These are planned locations, not existing files.

Alternatives considered:

- Per-account Durable Objects simplify live fanout but require cross-object delivery/reconciliation if they each own a mailbox. An open SSE response also prevents hibernation and incurs duration charges. Defer them until measured latency or scale requires signaling.
- A single all-accounts Durable Object can make mailbox changes atomic but becomes one application coordinator and remains billable while SSE is open. D1 already provides the required transaction boundary without that live coordinator.
- Worker + D1 with indexed pull, and optional bounded polling inside SSE, is the smallest complete implementation. Initial notification latency is about 15 seconds plus processing/network time; this is an explicit tradeoff, not an instant-push promise.

## 3. Trust boundary

Endpoints alone hold OpenPGP private keys and decrypt messages. Weave and Cloudflare can see account identifiers, recipient routing, friendship metadata, IP addresses, timing, envelope sizes, expiry, and transport status. Encryption does not hide this metadata or prevent denial of service, delay, deletion, traffic analysis, or endpoint compromise.

The relay is not the sole authority for a friend's encryption key. A new contact is accepted only after the endpoint verifies the expected full fingerprint through an independent trusted channel or a previously authenticated binding. The local test uses independently provisioned synthetic fingerprints. A self-signed key fetched from the same potentially malicious relay is not sufficient first-contact authentication. Unexpected key replacement fails closed; this increment does not silently rotate keys.

If an agent passes decrypted content into a hosted model, that model service is in the plaintext trust boundary. An SSE stream does not independently wake a hosted dot. Short-lived tool runtimes can pull when invoked; a host that supports persistent connections can consume SSE. Automatic wakeup requires that host's separately authorized inbound mechanism and is not demonstrated by the synthetic endpoint tests.

## 4. Selected cryptographic envelope

Use standard OpenPGP signed-and-encrypted messages, not a Weave cipher or ratchet. Pin the reference implementation to **OpenPGP.js 6.3.2** and the format to **RFC 9580**. Other languages/runtimes may use any implementation that passes the same byte-level and adversarial conformance fixtures; JavaScript is not mandatory for agents. The relay only verifies public authentication signatures and never decrypts message payloads. [Release](https://github.com/openpgpjs/openpgpjs/releases/tag/v6.3.2), [RFC 9580](https://www.rfc-editor.org/rfc/rfc9580.html)

The initial profile is deliberately narrow: v6 certificate/fingerprints, Ed25519 signing, X25519 encryption subkey, SHA-256 signatures, AES-128/OCB authenticated encryption with v6 PKESK and v2 SEIPD packets, and an uncompressed binary literal payload. No passwords, legacy unauthenticated encryption, v1 SEIPD fallback, compression, extra recipients, secret-key packets in public enrollment, or algorithm negotiation/downgrade are accepted. The full signing and encryption key hierarchy must be valid, unexpired, and unrevoked under the selected library. Public certificates may use pseudonymous labels and must not contain human email addresses by default.

For the pinned reference library, key generation explicitly selects the v6/AEAD feature set and the profile's algorithm preferences. The implementation must inspect generated keys and packets and verify conformance vectors; setting `aeadProtect` on an encrypt call alone is insufficient, because public-key message encryption follows recipient-key preferences. The library's v6-key compatibility warning makes exact pinning and upgrade conformance tests mandatory. This is a released library using a standardized format, not a claim that every OpenPGP client supports this profile. [OpenPGP.js v6 changes](https://github.com/openpgpjs/openpgpjs/wiki/v6-Changelog)

The binary literal is UTF-8 JSON containing `profile`, `fromAccount`, `toAccount`, `messageId`, `createdAt`, `expiresAt`, `a2aVersion`, and the A2A JSON-RPC request or response. All these fields are covered by the OpenPGP signature inside encryption. The receiver compares every duplicated outer field with the verified inner value before accepting anything. A reply has a new transport message ID, its own expiry, and the original A2A request ID in its JSON-RPC response; task/context IDs stay inside encryption.

Decryption supplies only the locally pinned expected sender verification key, requires a valid signature, fully consumes the bounded output and validates authenticated encryption before exposing plaintext, and rejects missing/extra-invalid signatures or mismatched identities. Set a finite 32 KiB decompression/output bound even though compressed packets are forbidden; do not inherit an unlimited library default while detecting malformed input. Do not treat successful parsing/decryption alone as sender authentication. Do not stream unverified plaintext to a model, log, parser with side effects, or tool.

**Security limit:** This asynchronous OpenPGP profile has no forward secrecy or post-compromise healing. A future private-key compromise can expose retained earlier ciphertext. ACK deletion is not cryptographic erasure. The profile is also not post-quantum secure. These limits are part of the design review, not hidden behind the phrase end-to-end encryption.

OpenPGP.js documents browser/Node support, not a blanket Cloudflare compatibility guarantee. Actual bounded, non-streaming public-signature verification under workerd, bundling limits, dependency/license checks, bounded malformed-key tests, and bidirectional vectors with an independent RFC 9580 implementation are release gates. The reference library is LGPL-3.0-or-later; preserve notices and check the repository's dependency policy before shipping. Historical library audits are not an audit of this composition. Private-key operations execute only in endpoint fixtures/runtimes. Do not weaken the profile if this gate fails; report and revise the written design. [Tagged dependency metadata](https://github.com/openpgpjs/openpgpjs/blob/v6.3.2/package.json)

## 5. Agent registration, login, and contacts

Each account is identified by its full v6 OpenPGP primary-key fingerprint. One certificate can have separate signing and encryption subkeys; every independent account uses a distinct certificate. The service never generates, accepts, logs, or backs up private keys. Losing the private key loses access in this increment; there is no server-side reset that can silently impersonate the account.

Registration/login uses a server-generated, single-use challenge. The signed bytes include a fixed `weave-auth/1` purpose, operation (`register` or `login`), exact HTTPS service origin, full account fingerprint, random 32-byte nonce, challenge ID, issue time, and two-minute expiry. The server returns the exact bytes to sign; the endpoint validates every field before using a standard detached OpenPGP signature. Signature verification and atomic nonce consumption must both succeed. Registration stores only the public certificate and returns the existing account for an identical re-enrollment; a conflicting key cannot replace it.

Login returns a cryptographically random 32-byte opaque bearer session with a 15-minute lifetime. Store only its digest and account/expiry/revocation metadata. No persistent refresh token is issued; re-login proves key possession again. Send the token in the Authorization header, never in a URL or message body. Each endpoint's host attaches credentials outside model-visible text. Authentication failures never fall back to anonymous access. SSE closes by session expiry and rechecks current account/session state on each polling cycle.

Friendship is account-to-account and requires both consents. An account creates a ten-minute, single-use invitation carrying a random request capability and its public fingerprint. Exchange it through the same independently authenticated channel used to verify fingerprints. The requesting account submits its proof-bound identity and consents; the invitation owner then accepts that exact verified peer and request ID/relationship generation. Every acceptance/removal carries the expected generation; a delayed mutation of an old request cannot affect a replacement relationship. Pending requests cannot deliver arbitrary chat bodies. No public account listing, inbox address directory, or stranger message delivery is provided.

Blocking/removing either consent prevents new admission and new delivery for that pair. Pending ciphertext for the pair becomes inaccessible immediately and is cleaned in bounded batches. Already transferred bytes and endpoint copies cannot be recalled. Re-friending creates a new relationship generation; old pending messages are never revived. Friendship and account identity confer no existing Weave governed-tool grants.

## 6. Public API

All routes below are Weave mailbox API v1, not A2A standard HTTP endpoints. JSON is bounded, rejects duplicate object keys/unknown security-bearing fields, and uses UTC timestamps. All protected operations derive the caller from the validated session rather than submitted account IDs.

| Route | Purpose and result |
| --- | --- |
| `POST /v1/auth/challenges` | Issue a bounded registration/login challenge; no account authority yet |
| `POST /v1/accounts` | Consume registration proof and return the public account identity |
| `POST /v1/sessions` | Consume login proof and return a short-lived session |
| `DELETE /v1/sessions/current` | Revoke the current session |
| `POST /v1/invitations` | Create the caller's private, expiring friend invitation |
| `POST /v1/friendships/requests` | Redeem an invitation and record the requester's consent |
| `GET /v1/friendships` | List only the caller's bounded pending/accepted contacts |
| `POST /v1/friendships/{peer}/accept` | Record consent for the exact request ID and expected relationship generation |
| `DELETE /v1/friendships/{peer}` | Revoke the expected relationship generation and prevent further delivery |
| `POST /v1/messages` | Atomically admit immutable ciphertext and return transport receipt/status |
| `GET /v1/inbox` | Pull unacknowledged, unexpired, currently permitted messages |
| `GET /v1/inbox/events` | SSE projection of the same pending inbox |
| `GET /v1/outbox` | List caller-owned transport status; never a second payload archive |
| `GET /v1/outbox/{messageId}` | Resolve a lost send response without resending new content |
| `POST /v1/inbox/{senderAccount}/{messageId}/ack` | Recipient accepts/burns that exact sender-scoped message; idempotent result |

A send includes `messageId` (UUIDv7), `toAccount`, `createdAt`, required `expiresAt`, `a2aVersion`, `envelopeProfile`, and base64-encoded binary OpenPGP ciphertext. The authenticated sender is bound server-side and repeated in the signed inner envelope. `createdAt` must equal the UUIDv7 timestamp. A new ID is admitted only within five minutes of that timestamp, with at most 30 seconds of future clock skew. This bounded first-admission window prevents an ancient retry recreating a payload after its tombstone has been purged.

The sender freezes the ID, fields, and exact ciphertext bytes before its first attempt. Identical duplicate submissions return the existing status; any meaningful mismatch returns `409 message_conflict`. A lost response is resolved by same-ID lookup/retry, never by automatically choosing a new ID or re-encrypting. Expired or already acknowledged IDs cannot refresh their expiry or regain ciphertext. The server never calls an endpoint URL embedded in a payload.

Errors distinguish malformed/oversized content (400/413), unauthenticated (401), forbidden operation (403), hidden/unavailable resource (404), conflicting immutable request (409), expired message (410 for an authorized known ID), and quota pressure (429 with bounded Retry-After). Cross-account lookups do not reveal whether another account's message exists. D1 failure yields an error, never a successful receipt before commit.

## 7. One durable record, two mailbox views

The message owner stores one authoritative row: sender, recipient, relationship generation, transport ID, immutable request digest, per-recipient sequence, creation/expiry timestamps, ciphertext, and terminal status/time. A unique `(sender, messageId)` constraint enforces sender-scoped idempotency. Inbox and outbox are indexed projections of this row; no second ciphertext copy or notification queue exists. Outbox returns metadata only.

Account/public-key records, challenges, hashed sessions, bilateral friendship records, and message rows are the bounded persistent model. Content-bearing A2A fields are not columns. Counters/indices needed for admission and recipient-local ordering stay in the same database and transaction. Sequence gaps after expiry/deletion are legitimate; sequence values are not authority. Admission consumes the sender's pending-outbox count/bytes, the recipient's pending-inbox count/bytes, and one global payload/retained-message slot. Terminalization releases both pending counters and global ciphertext bytes exactly once in the same transaction; the retained-message slot remains occupied until tombstone purge. An expired row awaiting cleanup may conservatively consume quota while being ineligible for delivery. Quota exhaustion cannot prevent clearing an existing payload or acknowledging it.

Message admission rechecks session/account validity, both consents and relationship generation, expiry, duplicate identity/digest, and account/global quotas within the atomic database operation. An application-level check followed later by an unrelated INSERT is insufficient. One INSERT establishes both mailbox views; any sequence/quota changes use the same D1 transaction. D1 `batch()` is transactional, but independently awaited calls are not. [D1 transaction API](https://developers.cloudflare.com/d1/worker-api/d1-database/)

Use primary D1 reads in v1, without read replicas or cached account/relationship authority. Replica session consistency alone cannot guarantee visibility of another session's ACK or revocation. No transaction remains open while a client reads or an agent decides what to do. [D1 read consistency](https://developers.cloudflare.com/d1/best-practices/read-replication/)

The earliest of recipient ACK, message expiry, or relationship/account revocation ends deliverability. Concurrent terminal transitions use conditional state changes; no terminal transition returns to pending. An ACK transaction clears the shared ciphertext and records minimal terminal metadata. Duplicate ACK returns the same terminal result and cannot affect another message or account. Every receipt, ACK and endpoint deduplication key binds the full `(senderAccount, messageId)` pair; two friends using the same UUID cannot suppress or burn each other's delivery.

## 8. Burn-after-read and expiry semantics

“Read” means the authenticated recipient endpoint has decrypted, verified, and accepted the message under its own local consumption policy. It does not mean a human read it, an agent completed a task, or a tool effect succeeded. GET, SSE emission, TCP delivery, and Last-Event-ID never burn a message.

The endpoint records a durable deduplication/acceptance checkpoint before ACK. If acceptance succeeded but ACK was lost, redelivery is safe to recognize and ACK again. This does not promise exactly-once task execution; a crash between local acceptance and an external tool effect still requires the host's own idempotency/outcome handling. The relay does not automatically execute anything.

Every payload has a mandatory immutable `expiresAt`. At and after that instant, new pull/SSE delivery and payload-bearing retry responses are forbidden using authoritative server time, even if the cleanup job has not run. The endpoint also rejects expired inner envelopes before exposing them to the agent. The first ACK of a still-pending message after expiry reports expired; it does not change expiry into a successful read receipt. Repeating an already committed ACK after its original expiry still returns its recorded acknowledged result, without a payload.

The proposed maximum message lifetime is 24 hours from its UUIDv7/creation timestamp; callers may choose any shorter future expiry. There is no implicit forever or default expiry. This maximum is a reviewable operating default, not a previously selected user requirement. A one-minute scheduled cleanup removes terminal/expired ciphertext in bounded indexed batches. Cleanup lag affects reclamation only, never delivery eligibility. Outages may delay physical cleanup; the read gate remains mandatory.

Retain only account-scoped ID/digest/status/expiry tombstones for seven days after terminal transition, then purge them. Retain no message text, A2A task/context IDs, decoded envelope, or decrypted content in tombstones. Exact-ID retry protection after that bounded period follows the stale UUIDv7 first-admission rejection, not an indefinite message history. A genuinely new ID is a new message; duplicate semantic tasks remain an endpoint responsibility.

ACK and expiry remove the one shared payload from active inbox/outbox delivery. They cannot recall ciphertext already read into another response, sent over the network, or stored by an endpoint. The receiver still checks expiry and duplicate IDs. No response cache, Worker Cache API, body logging, analytics payload capture, request-body tracing, or automatic database exports are enabled; responses use `Cache-Control: no-store`.

### Provider backup limitation and recovery

D1 Time Travel is always enabled and supports restoring earlier database contents for seven days on Free and 30 days on Paid. A DELETE does not remove earlier recovery history; the documented window is not an independently verified physical-erasure deadline. SQLite Durable Objects also have 30-day point-in-time recovery, so switching products does not solve this. There is no verified per-message history purge in the documented APIs. [D1 recovery](https://developers.cloudflare.com/d1/reference/time-travel/), [DO recovery](https://developers.cloudflare.com/durable-objects/api/sqlite-storage-api/)

The product promise is therefore: **ACK or expiry immediately stops active service access to ciphertext; provider recovery history may retain an earlier encrypted copy. Immediate erasure from every provider backup is not guaranteed.** Future endpoint-key compromise remains relevant because this profile lacks forward secrecy.

Production must never attach an old Time Travel restore directly to the live API. Restore only into a quarantined recovery procedure with ingress disabled. The v1 recovery procedure starts a fresh service epoch and fresh mailbox state; it does not import restored payloads, sessions, challenges, or friendships. Accounts re-enroll and contacts mutually consent again. This deliberately sacrifices unread messages rather than resurrecting burned content or rolled-back revocations. Any future selective recovery requires a separately reviewed deletion/revocation ledger. No backup restoration or destructive purge is authorized by writing this specification.

## 9. Pull, SSE, and bounded load

Pull returns at most 50 pending messages, ordered by recipient-local sequence, with a caller/account/epoch-scoped opaque scan cursor. It enforces a 256 KiB total response cap in addition to individual-message limits. The cursor is an untrusted paging position, not an authentication or ACK token. A fresh inbox scan always begins with the oldest still-pending message; clients may page but cannot cause an ACK by advancing a cursor.

SSE uses `text/event-stream`, emits the same envelope/ID/expiry fields as pull, and uses recipient sequence IDs. On connection/reconnection it performs pending replay; Last-Event-ID is only a scan hint and must not suppress an older unacknowledged message. Within one connection, already emitted IDs are not continuously re-emitted; on reconnection they remain eligible until acknowledged or terminal. Bound the per-connection replay set and close/reconnect instead of keeping unbounded memory.

The proposed initial stream polls an indexed pending range every 15 seconds and sends a heartbeat comment when idle. Rotate it at five minutes, token expiry, 20 polling cycles, bounded output/backpressure limits, or revocation, whichever occurs first. Two fixed D1 lease slots per account enforce the connection limit across Worker instances; acquire atomically with a new unique lease token, bind it to the session and a non-extendable stream deadline, and make every polling recheck and close/release conditional on that token. An old stream's delayed close cannot release a slot subsequently acquired by a new stream. A failed release can delay a new SSE connection until the five-minute lease expires, but cannot block ordinary pull or create an unbounded lease table. A slow/disconnected reader cancels polling and releases buffers. Reconnect uses exponential backoff with jitter, capped at 30 seconds. Pull remains usable without any background process or permanent connection.

Workers can stream HTTP responses without a fixed wall-clock limit while the client is connected, but runtime upgrades can terminate requests after a 30-second grace period. CPU/subrequest limits still apply. `waitUntil()` is not a durable delivery mechanism and has a 30-second post-response/disconnect limit; admission and ACK must await their database commit. No live connection is a durable queue. [Workers limits](https://developers.cloudflare.com/workers/platform/limits/), [streaming API](https://developers.cloudflare.com/workers/runtime-apis/streams/)

Reviewable initial limits: 64 KiB binary ciphertext; 32 KiB verified inner JSON; depth 16; 16 KiB public certificate; 4 KiB detached auth signature; 50 messages/256 KiB per pull; 1 MiB emitted per SSE connection; two concurrent streams/account; 100 messages or 1 MiB separately in each account's pending inbox and pending outbox; 100 admitted messages/minute/account; 100 contacts and 10 pending invitations/account. Account limits alone are insufficient because an agent may self-register several accounts.

Pilot-wide hard bounds are 100 accounts, 32 MiB pending ciphertext, 8,000 retained message rows including tombstones, 1,000 new message admissions per rolling 24 hours, 500 live/retained challenges (at most five/fingerprint), 400 session rows (at most four/account), 200 fixed SSE lease slots, 1,000 invitation/request rows and 5,000 relationship rows. Challenges/invitations/sessions are removed after expiry; counters and cleanup cannot grow an unbounded auxiliary ledger. Rate-limit counters use fixed bounded buckets, not per-request records. If a hard metadata limit is reached, deny new admission rather than evict unexpired tombstones or extend retention. Registration is additionally capped at 10 new accounts per service-wide hour and one challenge issuance per fingerprint per ten seconds. These operating limits are reviewable defaults, not a verified-human or Sybil-prevention claim; hostile registration can still cause denial of service. Database/index size and scan cost remain measured acceptance criteria.

Cloudflare Paid is the recommended eventual pilot tier, subject to account/budget approval: Workers starts at $5/month with 10 million inbound requests and 30 million CPU-ms included; waiting duration is not separately charged. D1 includes 25 billion row reads, 50 million row writes and 5 GB storage, then usage charges. Free has strict daily quotas and only 10 ms Worker CPU per invocation, so this design does not promise a free always-on service. [Workers pricing](https://developers.cloudflare.com/workers/platform/pricing/), [D1 pricing](https://developers.cloudflare.com/d1/platform/pricing/)

Illustrative model, not a benchmark: 100 five-minute SSE streams with 15-second checks generate 17.28 million internal polls and 864,000 reconnect requests per 30 days. At an assumed 1 ms CPU/poll and 5 ms/reconnect, CPU is 21.6 million ms before other work. Real crypto verification, scanned rows, indices, and cleanup must be measured. An indexed query is essential; D1 bills scanned rows rather than poll count. A paid D1 database has a 10 GB cap and serializes its queries; the small pilot is not evidence of unlimited scale. [D1 limits](https://developers.cloudflare.com/d1/platform/limits/)

## 10. A2A interoperability boundary

Pin released **A2A specification v1.0.1**, whose wire revision is **1.0**, and **v0.3.0**, whose maintained wire revision is **0.3**. Keep separate fixtures/codecs for the two revisions; do not relabel one JSON shape as both. The mailbox protocol has its own version `weave-mailbox/1` and the envelope its own profile identifier. [A2A 1.0.1](https://a2a-protocol.org/v1.0.1/specification/), [A2A 0.3.0](https://a2a-protocol.org/v0.3.0/specification/)

The initial endpoint conformance profile covers these selected JSON-RPC payloads:

| Interaction | A2A 1.0 | A2A 0.3 |
| --- | --- | --- |
| Send a message | `SendMessage` / `SendMessageRequest` | `message/send` / `MessageSendParams` |
| Receive its result | `result` contains exactly one `message` or `task` | `result` is the revision's `Message` or `Task` object |
| Query an endpoint-owned task | `GetTask` | `tasks/get` |
| Request cancellation | `CancelTask` | `tasks/cancel` |

A2A message ID, request ID, context ID, task ID, roles, text parts, Task/TaskStatus, and revision-specific errors preserve their standard meaning inside the envelope. The initial supported content is text parts only, bounded by the inner envelope limit. Task IDs are scoped by the owning endpoint and peer relationship; knowing an ID grants no access. An endpoint may return a direct Message without creating a Task. If it creates a task, its host owns durable state, authorization, GetTask results, cancellation decisions, and terminal-state rules. The relay owns none of those semantics.

The end-to-end fixture must demonstrate direct message response plus one deterministic endpoint-owned task progressing from submitted/working to completed, GetTask, cancellation of a cancellable task, and proper unknown/not-cancellable task errors in both revisions. This synthetic task executor performs no real tools or external effects. Unsupported methods, part types, artifacts, push notifications, subscriptions, extension requirements, and versions produce explicit endpoint-side errors rather than fabricated support.

Every A2A request and response is sent as an independent encrypted mailbox message with its own transport ID and expiry. JSON-RPC request IDs provide correlation; they are not the mailbox's idempotency key. The endpoint persists its own deduplication/outcome record before effects. Request expiry or missing response does not prove task failure or cancellation; it can leave an unknown outcome. ACK of a request only confirms recipient acceptance, and ACK of a response only confirms its delivery. `202 queued` from Weave is never an A2A result or task completion.

This is an **encrypted mailbox transport for selected A2A payloads**, not a claim of a fully conformant A2A HTTP/JSON-RPC server or complete custom binding. A2A 1.0 custom bindings require all core operations; this narrow profile does not satisfy that broader claim. A generic A2A client therefore needs a transport/encryption adapter or compatible host. No mandatory Weave client executable is implied. [A2A custom-binding requirements](https://a2a-protocol.org/v1.0.1/specification/#12-custom-binding-guidelines)

Do not publish the relay URL as an ordinary `JSONRPC` or `HTTP+JSON` AgentCard interface. The first version exchanges private Weave peer descriptors containing the pinned fingerprint, the two supported payload revisions, exact operation/part subset, and mailbox profile. It publishes no standard AgentCard for the relay. A future real A2A endpoint can publish its own truthful AgentCard after its standard binding is implemented and tested. Mailbox SSE is not A2A `SendStreamingMessage`, task subscription, or an AgentCard `streaming: true` promise.

## 11. Acceptance evidence required before claiming it works

All tests start with synthetic identities/data and a controllable clock. Real external agents, credentials, owner identities, chain accounts, or public deployments are not needed for local acceptance.

| ID | Required observable outcome |
| --- | --- |
| M01 | Two endpoints independently enroll and log in; wrong key, wrong origin/purpose, stale challenge and concurrent challenge reuse fail without a session |
| M02 | A second account on the same endpoint has separate keys, contacts, quotas and inbox; another account cannot list, fetch, ACK or infer its message status |
| M03 | Pending/one-sided friendship cannot send; mutual consent enables exactly that pair; revoked/old generations cannot send or deliver; delayed acceptance/removal cannot mutate a replacement generation |
| M04 | Relay/D1/log inspection contains only ciphertext and declared metadata; a relay operator cannot decrypt the two-endpoint test message |
| M05 | Expected-fingerprint substitution, tampered ciphertext/signature, inner/outer sender/recipient/ID/expiry/version mismatch and unsupported crypto packets all fail before plaintext reaches the synthetic agent |
| M06 | Real workerd verifies the selected OpenPGP authentication profile; exact pinned-library positive/negative vectors pass under resource limits, without private keys entering the Worker |
| M07 | Concurrent identical sends create one row/sequence/quota charge; changed ciphertext/recipient/expiry with the same sender ID conflicts; equal UUIDs from different friends remain distinct through delivery, dedup and ACK |
| M08 | Commit followed by lost HTTP response resolves by same-ID retry/lookup; no second ciphertext is admitted; a database failure cannot return queued |
| M09 | Pull and SSE expose the same pending IDs/bytes/expiry; pagination cannot cross account/epoch boundaries or lose unacknowledged entries |
| M10 | Disconnect after SSE delivery but before ACK, restart and Last-Event-ID resume still recover the pending message; durable endpoint dedup avoids a second synthetic execution |
| M11 | Recipient ACK clears shared ciphertext from active delivery/outbox; sender/stranger ACK fails; lost/concurrent/repeated ACK is idempotent, including retry after the original expiry |
| M12 | Exact expiry boundary blocks pull, SSE and retry payload access before cleanup; missing/extended/out-of-range expiry is rejected; endpoint discards an in-flight message that expires before acceptance |
| M13 | ACK-versus-expiry/revocation races have one terminal result and never restore pending; already buffered bytes are explicitly outside recall guarantees |
| M14 | Cleanup deletes expired/terminal ciphertext despite reconnects, in bounded batches; seven-day tombstone purge does not allow a stale UUIDv7 ID to be newly admitted |
| M15 | Slow SSE readers, disconnect, token expiry and revocation stop polling/buffering; two lease slots hold across Worker instances, recover from lost release and reject delayed old-token release after reacquisition; 5-minute/20-cycle/output caps and backoff are enforced |
| M16 | Two exact A2A revisions independently pass message response, Task/GetTask/cancel/error fixtures, including differing JSON shapes; transport ACK is never interpreted as completed task |
| M17 | Unknown A2A methods/versions, wrong roles/part types, malformed/deep/oversized JSON and expansion attempts fail with bounded output; mailbox relay does not execute payload instructions |
| M18 | Sender/recipient/global counters release once on terminalization; hard bounds include tombstones, challenges, sessions and contacts; quota exhaustion still permits ACK/cleanup; invitation expiry/reuse and malformed oversized public keys fail closed without payload/secret logs |
| M19 | Quarantined historical-state recovery cannot expose previously burned messages, resurrect sessions or friendships, or reuse old cursors; unread restored payload loss is reported honestly |
| M20 | Local deterministic tests and actual D1 transaction/concurrency behavior are distinguished; an authorized Cloudflare staging run later verifies disconnect/redeploy, primary-read ACK visibility and cleanup scheduling |
| M21 | Measured CPU, scanned rows, storage, connection limits and dependency/license/security checks support the chosen operating limits; a pricing model alone does not pass this gate |
| M22 | Documentation states no forward secrecy, no immediate all-backup erasure, unconfigured chain identity and no demonstrated hosted-dot wakeup; no broad A2A compatibility or exactly-once claim appears |

Local workerd and SQLite emulation do not prove production Time Travel or platform lifecycle behavior. Record exact commits, commands, fixtures, failing/skipped gates and staging evidence separately. No local or staging tests have run for this specification-only change.

## 12. Deployment readiness and review handoff

Read-only inspection of the baseline found no Wrangler/Cloudflare configuration and no Cloudflare steps in the repository's 13 workflow files. The existing publisher factory offers Docker Compose, Kubernetes, Nomad, Fly.io and GitHub Actions; it does not offer Cloudflare. Existing built-in Host authentication is a configured shared API-key/bearer mechanism, not this account registration/login flow. [Publisher factory](../../../hosts/Weave.Cli/Commands/Workspace/WorkspacePublisherFactory.cs), [existing authentication configuration](../../../hosts/Weave.Host/Security/ApiAuthOptions.cs)

No Cloudflare connector was found in the current tool catalog/plugin search. No Cloudflare account, plan, domain, resource inventory or authenticated deployment session has been verified; secret values were not requested or inspected. The next authorized implementation can prepare local workerd tests and a draft code PR without claiming deployment. Creating service resources, new credentials/persistent access, paying for a plan, or publishing a real endpoint requires the relevant account and explicit scoped authorization. Do not substitute another hosting provider.

Before implementation: review this written specification, especially the OpenPGP no-forward-secrecy limit, D1 recovery-history limitation, 24-hour maximum TTL/seven-day tombstone defaults, 15-second SSE polling tradeoff, and the selected A2A interoperability subset. Then produce the implementation plan and execution-method review. Continue read-only source/environment readiness work while review is pending; do not silently resume the superseded A2A plan.

The next implementation evidence should be the smallest complete synthetic path, followed by the negative/recovery cases above. Progress reports should state concrete completed checks, the current blocker or next result, and whether evidence is local or deployed. Long-running checks remain open until terminal or a verified access/approval boundary is reached.
